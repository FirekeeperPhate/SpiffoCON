-- SpiffoCON Bridge: lets the SpiffoCON admin tool read what RCON can't (player positions,
-- inventories, vehicles, world state) and do a few admin actions (heal, remove items, repair,
-- refuel or remove vehicles). Server side only; it does nothing on clients.
--
-- Channel: files in the server's Zomboid/Lua folder, which SpiffoCON reads and writes over SFTP.
--   spiffocon_in.txt   written by SpiffoCON:  "SEQ <n>" then one request per line: id<TAB>action<TAB>arg...
--   spiffocon_out.txt  written here:          "SEQ <n>", one JSON reply per line, "END <n>"
-- A batch is handled once (the highest SEQ seen); requests already in the file when the server
-- starts are ignored, so nothing runs twice after a restart.
if not isServer() then return end

local VERSION = 2
local IN_FILE = "spiffocon_in.txt"
local OUT_FILE = "spiffocon_out.txt"
local POLL_MS = 1000

local lastSeq = nil
local nextPoll = 0

-- ---- JSON ----

local function jsonString(s)
	s = tostring(s)
	s = s:gsub('\\', '\\\\'):gsub('"', '\\"'):gsub('\n', '\\n'):gsub('\r', '\\r'):gsub('\t', '\\t')
	return '"' .. s .. '"'
end

local encode
local function isArray(t)
	if t.__array then return true end
	return #t > 0
end

encode = function(v)
	local kind = type(v)
	if v == nil then return "null" end
	if kind == "boolean" then return v and "true" or "false" end
	if kind == "number" then
		if v ~= v or v == math.huge or v == -math.huge then return "null" end
		if v == math.floor(v) and math.abs(v) < 1e15 then return string.format("%d", v) end
		return tostring(v)
	end
	if kind == "table" then
		local parts = {}
		if isArray(v) then
			for i = 1, #v do parts[#parts + 1] = encode(v[i]) end
			return "[" .. table.concat(parts, ",") .. "]"
		end
		for k, item in pairs(v) do
			if k ~= "__array" then parts[#parts + 1] = jsonString(k) .. ":" .. encode(item) end
		end
		return "{" .. table.concat(parts, ",") .. "}"
	end
	return jsonString(v)
end

local function array() return { __array = true } end

-- calls f and returns its result, or nil if the API is missing or fails
local function try(f, ...)
	local ok, result = pcall(f, ...)
	if ok then return result end
	return nil
end

-- ---- actions ----

local function findPlayer(username)
	local players = getOnlinePlayers()
	for i = 0, players:size() - 1 do
		local p = players:get(i)
		if p:getUsername() == username then return p end
	end
	return nil
end

local function playerInfo(p)
	local info = {
		username = try(function() return p:getUsername() end),
		displayName = try(function() return p:getDisplayName() end),
		x = try(function() return math.floor(p:getX()) end),
		y = try(function() return math.floor(p:getY()) end),
		z = try(function() return math.floor(p:getZ()) end),
		dead = try(function() return p:isDead() end),
		health = try(function() return math.floor(p:getBodyDamage():getOverallBodyHealth()) end),
		god = try(function() return p:isGodMod() end),
		invisible = try(function() return p:isInvisible() end),
		noclip = try(function() return p:isNoClip() end),
		hoursSurvived = try(function() return math.floor(p:getHoursSurvived() * 10) / 10 end),
		zombieKills = try(function() return p:getZombieKills() end),
		profession = try(function() return p:getDescriptor():getProfession() end),
		steamId = try(function() return tostring(p:getSteamID()) end),
	}
	info.role = try(function() return p:getRole():getName() end) or try(function() return p:getAccessLevel() end)
	local vehicle = try(function() return p:getVehicle() end)
	if vehicle then
		info.vehicle = try(function() return vehicle:getScript():getFullName() end)
	end
	return info
end

local function listPlayers()
	local result = array()
	local players = getOnlinePlayers()
	for i = 0, players:size() - 1 do
		result[#result + 1] = playerInfo(players:get(i))
	end
	return result
end

-- items grouped by container and type: { container, fullType, name, count, equipped }
local function addItems(result, container, label, player)
	local items = container:getItems()
	local grouped = {}
	local order = {}
	for i = 0, items:size() - 1 do
		local item = items:get(i)
		local fullType = item:getFullType()
		local entry = grouped[fullType]
		if not entry then
			entry = { container = label, fullType = fullType, name = try(function() return item:getDisplayName() end) or fullType, count = 0, equipped = false }
			grouped[fullType] = entry
			order[#order + 1] = fullType
		end
		entry.count = entry.count + 1
		if player and (try(function() return player:isEquipped(item) end) or try(function() return item:isEquipped() end)) then
			entry.equipped = true
		end
		-- bags and other containers: list what is inside them too
		local inner = try(function() return item:getInventory() end)
		if inner then
			addItems(result, inner, label .. " > " .. entry.name, nil)
		end
	end
	for _, fullType in ipairs(order) do result[#result + 1] = grouped[fullType] end
end

local function inventory(username)
	local p = findPlayer(username)
	if not p then error("player " .. tostring(username) .. " is not online") end
	local result = array()
	addItems(result, p:getInventory(), "Inventory", p)
	return result
end

local function vehicles()
	local result = array()
	local list = getCell():getVehicles()
	for i = 0, list:size() - 1 do
		local v = list:get(i)
		local driver = try(function() return v:getDriver():getUsername() end)
		result[#result + 1] = {
			id = try(function() return v:getId() end),
			script = try(function() return v:getScript():getFullName() end),
			x = try(function() return math.floor(v:getX()) end),
			y = try(function() return math.floor(v:getY()) end),
			z = try(function() return math.floor(v:getZ()) end),
			driver = driver,
			engineRunning = try(function() return v:isEngineRunning() end),
		}
	end
	return result
end

local function world()
	local t = getGameTime()
	local climate = getClimateManager()
	return {
		year = try(function() return t:getYear() end),
		month = try(function() return t:getMonth() + 1 end),
		day = try(function() return t:getDay() + 1 end),
		hour = try(function() return t:getHour() end),
		minute = try(function() return t:getMinutes() end),
		worldAgeHours = try(function() return math.floor(t:getWorldAgeHours()) end),
		temperature = try(function() return math.floor(climate:getTemperature() * 10) / 10 end),
		rain = try(function() return math.floor(climate:getRainIntensity() * 100) / 100 end),
		fog = try(function() return math.floor(climate:getFogIntensity() * 100) / 100 end),
		zombiesLoaded = try(function() return getCell():getZombieList():size() end),
		players = try(function() return getOnlinePlayers():size() end),
	}
end

-- ---- write actions ----
-- Each one mirrors what the game's own server code does for the same admin action, including
-- the call that syncs the change to the player's client, and is written to the admin log.

local function audit(text)
	try(function() writeLog("admin", "SpiffoCON bridge: " .. text) end)
end

local function requirePlayer(username)
	local p = findPlayer(username)
	if not p then error("player " .. tostring(username) .. " is not online") end
	return p
end

-- as the health cheat "healthFullBody" in server/ClientCommands.lua
local function heal(username)
	local p = requirePlayer(username)
	local parts = p:getBodyDamage():getBodyParts()
	for i = 0, parts:size() - 1 do
		local part = parts:get(i)
		part:RestoreToFullHealth()
		if part:getStiffness() > 0 then
			part:setStiffness(0)
			try(function() p:getFitness():removeStiffnessValue(BodyPartType.ToString(part:getType())) end)
		end
		syncBodyPart(part, 0xFFFFFFFFFFF)
	end
	audit("healed " .. username)
	return { health = try(function() return math.floor(p:getBodyDamage():getOverallBodyHealth()) end) }
end

local function isWornOrAttached(p, item)
	return try(function() return p:isEquippedClothing(item) end)
		or try(function() return p:getWornItems():contains(item) end)
		or try(function() return p:isAttachedItem(item) end)
end

local function collect(container, fullType, into)
	local items = container:getItems()
	for i = 0, items:size() - 1 do
		local item = items:get(i)
		if item:getFullType() == fullType then into[#into + 1] = item end
		local inner = try(function() return item:getInventory() end)
		if inner then collect(inner, fullType, into) end
	end
end

-- as buildUtil in server/BuildingObjects/ISBuildUtil.lua; count 0 = all of them
local function removeItem(username, fullType, count)
	local p = requirePlayer(username)
	count = tonumber(count) or 0
	local found = {}
	collect(p:getInventory(), fullType, found)
	local removed, skipped = 0, 0
	for _, item in ipairs(found) do
		if count > 0 and removed >= count then break end
		if isWornOrAttached(p, item) then
			skipped = skipped + 1
		else
			try(function() p:removeFromHands(item) end)
			local container = item:getContainer() or p:getInventory()
			container:Remove(item)
			sendRemoveItemFromContainer(container, item)
			removed = removed + 1
		end
	end
	if removed > 0 then audit("removed " .. removed .. " x " .. fullType .. " from " .. username) end
	return { removed = removed, skippedWorn = skipped }
end

local function requireVehicle(id)
	local v = getVehicleById(tonumber(id) or -1)
	if not v then error("no vehicle with id " .. tostring(id) .. " (it may be in an area no player has loaded)") end
	return v
end

local function vehicleName(v)
	return (try(function() return v:getScript():getFullName() end) or "vehicle") .. " #" .. tostring(try(function() return v:getId() end))
end

-- as Commands.repair in server/Vehicles/VehicleCommands.lua
local function repairVehicle(id)
	local v = requireVehicle(id)
	v:repair()
	audit("repaired " .. vehicleName(v))
	return { repaired = true }
end

-- as Commands.setContainerContentAmount, filling the gas tank to its capacity
local function refuelVehicle(id)
	local v = requireVehicle(id)
	local tank = v:getPartById("GasTank")
	if not tank then error(vehicleName(v) .. " has no gas tank") end
	local capacity = tank:getContainerCapacity()
	tank:setContainerContentAmount(capacity)
	v:transmitPartModData(tank)
	audit("refuelled " .. vehicleName(v))
	return { fuel = capacity }
end

-- as Commands.remove
local function removeVehicle(id)
	local v = requireVehicle(id)
	local name = vehicleName(v)
	v:permanentlyRemove()
	audit("removed " .. name)
	return { removed = true }
end

local actions = {
	ping = function() return { version = VERSION, players = getOnlinePlayers():size() } end,
	players = function() return listPlayers() end,
	inventory = function(username) return inventory(username) end,
	vehicles = function() return vehicles() end,
	world = function() return world() end,
	heal = heal,
	removeitem = removeItem,
	repairvehicle = repairVehicle,
	refuelvehicle = refuelVehicle,
	removevehicle = removeVehicle,
}

-- ---- channel ----

local function split(line)
	local parts = {}
	for part in (line .. "\t"):gmatch("([^\t]*)\t") do parts[#parts + 1] = part end
	return parts
end

-- returns seq, { {id, action, args...} }
local function readRequests()
	local reader = getFileReader(IN_FILE, false)
	if not reader then return nil, nil end
	local seq = nil
	local requests = {}
	local line = reader:readLine()
	while line do
		if not seq then
			seq = tonumber(line:match("^SEQ (%d+)"))
		elseif line ~= "" then
			requests[#requests + 1] = split(line)
		end
		line = reader:readLine()
	end
	reader:close()
	return seq, requests
end

local function handle(seq, requests)
	local lines = { "SEQ " .. seq }
	for _, request in ipairs(requests) do
		local id, action = request[1], request[2]
		local handler = actions[action or ""]
		local reply = { id = id }
		if not handler then
			reply.ok = false
			reply.error = "unknown action " .. tostring(action)
		else
			local ok, result = pcall(handler, request[3], request[4], request[5])
			reply.ok = ok
			if ok then reply.data = result else reply.error = tostring(result) end
		end
		lines[#lines + 1] = encode(reply)
	end
	lines[#lines + 1] = "END " .. seq
	local writer = getFileWriter(OUT_FILE, true, false)
	writer:write(table.concat(lines, "\n") .. "\n")
	writer:close()
end

local function poll()
	local now = getTimestampMs()
	if now < nextPoll then return end
	nextPoll = now + POLL_MS
	local ok, err = pcall(function()
		local seq, requests = readRequests()
		if lastSeq == nil then
			lastSeq = seq or 0 -- whatever was there before this start is old
			return
		end
		if seq and seq > lastSeq then
			lastSeq = seq
			handle(seq, requests)
		end
	end)
	if not ok then print("SpiffoCON bridge: " .. tostring(err)) end
end

Events.OnTick.Add(poll)
print("SpiffoCON bridge " .. VERSION .. " loaded")
