-- SpiffoCON Bridge: lets the SpiffoCON admin tool read what RCON can't (player positions,
-- inventories, vehicles, world state) and do a few admin actions (heal, remove items, repair,
-- refuel or remove vehicles). Server side only; it does nothing on clients.
--
-- Channel: files in the server's Zomboid/Lua folder, which SpiffoCON reads and writes over SFTP.
--   spiffocon_in.txt   written by SpiffoCON:  "SEQ <n>", one request per line (id<TAB>action<TAB>arg...), "END <n>"
--   spiffocon_out.txt  written here:          "SEQ <n>", one JSON reply per line, "END <n>"
-- A batch is handled once (each new SEQ); requests already in the file when the server
-- starts are ignored, so nothing runs twice after a restart.
if not isServer() then return end

local VERSION = 3
local IN_FILE = "spiffocon_in.txt"
local OUT_FILE = "spiffocon_out.txt"
local POLL_MS = 1000

local lastSeq = nil
local nextPoll = 0

-- ---- JSON ----

local CONTROL_ESCAPES = { [10] = '\\n', [13] = '\\r', [9] = '\\t' }

-- every control character must be escaped, or SpiffoCON can't read the reply
local function jsonString(s)
	s = tostring(s)
	s = s:gsub('\\', '\\\\'):gsub('"', '\\"')
	s = s:gsub('%c', function(c)
		local b = c:byte()
		return CONTROL_ESCAPES[b] or string.format('\\u%04x', b)
	end)
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

-- the items inside a bag; nil for any other item (checked first: an error per plain item would
-- cost a Java exception each)
local function containerOf(item)
	if instanceof(item, "InventoryContainer") then
		return try(function() return item:getInventory() end)
	end
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
		-- B42 keeps a CharacterProfession object (getProfession is gone)
		profession = try(function()
			local cp = p:getDescriptor():getCharacterProfession()
			return cp and (try(function() return cp:getName() end) or tostring(cp))
		end) or try(function() return p:getDescriptor():getProfession() end),
		-- as a string: a Java long through Lua is a double and loses the last digits
		steamId = try(function() return getSteamIDFromUsername(p:getUsername()) end),
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
		local inner = containerOf(item)
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
		-- no error for the usual empty car (an error costs a Java exception each time)
		local seat = try(function() return v:getDriver() end)
		local driver = seat and try(function() return seat:getUsername() end)
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

-- walks the inventory like addItems, so labels match the ones SpiffoCON shows
-- ("Inventory", "Inventory > Backpack"); only items in the container named wanted count
-- (nil: anywhere)
local function collect(container, label, fullType, wanted, into)
	local items = container:getItems()
	local names = {}
	for i = 0, items:size() - 1 do
		local item = items:get(i)
		local itemType = item:getFullType()
		if itemType == fullType and (wanted == nil or wanted == label) then into[#into + 1] = item end
		-- addItems names a bag after the first item of its type in this container
		names[itemType] = names[itemType] or (try(function() return item:getDisplayName() end) or itemType)
		local inner = containerOf(item)
		if inner then collect(inner, label .. " > " .. names[itemType], fullType, wanted, into) end
	end
end

-- as buildUtil in server/BuildingObjects/ISBuildUtil.lua; count 0 = all of them; container
-- (bridge v3) limits it to the items listed under that container
local function removeItem(username, fullType, count, container)
	local p = requirePlayer(username)
	-- a missing count must not mean "all of them" (a cut request line would remove everything)
	count = tonumber(count)
	if not count then error("removeitem needs a count (0 = all)") end
	if container == "" then container = nil end
	local found = {}
	collect(p:getInventory(), "Inventory", fullType, container, found)
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

-- ids are runtime ids, given again to other vehicles as areas unload and load: when SpiffoCON
-- says which model it means (bridge v3), a different vehicle under that id is refused
local function requireVehicle(id, expectedScript)
	local v = getVehicleById(tonumber(id) or -1)
	if not v then error("no vehicle with id " .. tostring(id) .. " (it may be in an area no player has loaded)") end
	if expectedScript and expectedScript ~= "" then
		local script = try(function() return v:getScript():getFullName() end)
		if script ~= expectedScript then
			error("vehicle #" .. tostring(id) .. " is now a " .. tostring(script) .. ", not " .. expectedScript .. ": refresh the list")
		end
	end
	return v
end

local function vehicleName(v)
	return (try(function() return v:getScript():getFullName() end) or "vehicle") .. " #" .. tostring(try(function() return v:getId() end))
end

-- as Commands.repair in server/Vehicles/VehicleCommands.lua
local function repairVehicle(id, script)
	local v = requireVehicle(id, script)
	v:repair()
	audit("repaired " .. vehicleName(v))
	return { repaired = true }
end

-- as Commands.setContainerContentAmount, filling the gas tank to its capacity
local function refuelVehicle(id, script)
	local v = requireVehicle(id, script)
	local tank = v:getPartById("GasTank")
	if not tank then error(vehicleName(v) .. " has no gas tank") end
	local capacity = tank:getContainerCapacity()
	tank:setContainerContentAmount(capacity)
	v:transmitPartModData(tank)
	audit("refuelled " .. vehicleName(v))
	return { fuel = capacity }
end

-- as Commands.remove
local function removeVehicle(id, script)
	local v = requireVehicle(id, script)
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

-- returns seq, { {id, action, args...} }, complete: a batch counts only once its "END <seq>" line
-- is there (bridge v3), so a file read while it was being written is not run half
local function readRequests()
	local reader = getFileReader(IN_FILE, false)
	if not reader then return nil, nil, false end
	local seqText = nil
	local requests = {}
	local complete = false
	local line = reader:readLine()
	while line do
		if not seqText then
			seqText = line:match("^SEQ (%d+)")
		elseif line == "END " .. seqText then
			complete = true
		elseif line ~= "" then
			requests[#requests + 1] = split(line)
		end
		line = reader:readLine()
	end
	reader:close()
	return tonumber(seqText), requests, complete
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
			local ok, result = pcall(handler, request[3], request[4], request[5], request[6])
			reply.ok = ok
			if ok then reply.data = result else reply.error = tostring(result) end
		end
		-- a reply that can't be encoded still answers, or SpiffoCON would wait for nothing
		local encoded, line = pcall(encode, reply)
		if not encoded then
			line = encode({ id = id, ok = false, error = "the bridge could not encode its reply: " .. tostring(line) })
		end
		lines[#lines + 1] = line
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
		local seq, requests, complete = readRequests()
		if lastSeq == nil then
			lastSeq = seq or 0 -- whatever was there before this start is old
			return
		end
		-- any new number is a new batch: "greater than" would ignore a PC whose clock is behind
		if seq and complete and seq ~= lastSeq then
			lastSeq = seq
			handle(seq, requests)
		end
	end)
	if not ok then print("SpiffoCON bridge: " .. tostring(err)) end
end

-- after a Lua reload the previous copy's poller must go, or every batch would run twice
if SpiffoCONBridgePoll then Events.OnTick.Remove(SpiffoCONBridgePoll) end
SpiffoCONBridgePoll = poll
Events.OnTick.Add(poll)
print("SpiffoCON bridge " .. VERSION .. " loaded")
