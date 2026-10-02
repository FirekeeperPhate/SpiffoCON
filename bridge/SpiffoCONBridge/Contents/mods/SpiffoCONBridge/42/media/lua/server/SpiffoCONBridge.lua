-- SpiffoCON Bridge: lets the SpiffoCON admin tool read what RCON can't (player positions,
-- inventories, vehicles, world state) and do a few admin actions (heal, remove items, repair,
-- refuel or remove vehicles, set the weather, remove zombie corpses). Server side only; it does nothing on clients.
--
-- Channel: files in the server's Zomboid/Lua folder, which SpiffoCON reads and writes over SFTP.
--   spiffocon_in.txt   written by SpiffoCON:  "SEQ <n>", one request per line (id<TAB>action<TAB>arg...), "END <n>"
--   spiffocon_out.txt  written here:          "SEQ <n>", one JSON reply per line, "END <n>"
-- A batch is handled once (each new SEQ); requests already in the file when the server
-- starts are ignored, so nothing runs twice after a restart.
if not isServer() then return end

local VERSION = 6
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

-- B42's IsoCell.getVehicles() is a java.util.Set (B41: an ArrayList), which has no get(i):
-- copied into an ArrayList, the list type the game's own Lua builds with ArrayList.new()
local function loadedVehicles()
	local vehicles = getCell():getVehicles()
	local list = ArrayList.new()
	list:addAll(vehicles)
	return list
end

local function vehicles()
	local result = array()
	local list = loadedVehicles()
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

-- ---- climate (bridge v5) ----
-- The admin overrides of the game's own Climate panel (client/ISUI/AdminPanel/ISAdmPanelClimate.lua),
-- set here on the server: it sends the resulting weather to every client at each ten-minute
-- climate tick (ClimateManager.update). The game saves them with the world until they are reset.

local FLOAT_PRECIPITATION, FLOAT_TEMPERATURE, FLOAT_FOG, FLOAT_WIND, FLOAT_CLOUDS = 3, 4, 5, 6, 8
local BOOL_IS_SNOW = 0

local function round(v, step)
	if v == nil then return nil end
	return math.floor(v / step + 0.5) * step
end

-- the admin value of a climate float, or nil when it is not overridden
local function adminValue(index)
	local f = getClimateManager():getClimateFloat(index)
	if f and f:isEnableAdmin() then return f:getAdminValue() end
	return nil
end

-- nil turns the override off; a value is kept within the float's own range
local function setAdmin(index, value)
	local f = getClimateManager():getClimateFloat(index)
	if value == nil then
		f:setEnableAdmin(false)
	else
		f:setEnableAdmin(true)
		f:setAdminValue(math.max(f:getMin(), math.min(f:getMax(), value)))
	end
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
		clouds = try(function() return round(climate:getCloudIntensity(), 0.01) end),
		wind = try(function() return round(climate:getWindspeedKph(), 0.1) end),
		snow = try(function() return climate:getPrecipitationIsSnow() end),
		-- bridge v5: what is overridden (absent when the weather decides)
		adminFog = try(function() return round(adminValue(FLOAT_FOG), 0.01) end),
		adminClouds = try(function() return round(adminValue(FLOAT_CLOUDS), 0.01) end),
		adminWind = try(function()
			local v = adminValue(FLOAT_WIND)
			return v and round(v * climate:getMaxWindspeedKph(), 0.1)
		end),
		adminTemperature = try(function() return round(adminValue(FLOAT_TEMPERATURE), 0.1) end),
		adminSnow = try(function()
			if not climate:getClimateBool(BOOL_IS_SNOW):isEnableAdmin() then return nil end
			return round(adminValue(FLOAT_PRECIPITATION), 0.01)
		end),
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

-- climate <setting> <value | off>: fog, clouds and snow 0-1, wind in km/h, temperature in °C;
-- "climate reset" gives the weather back to the game
local function setClimate(name, value)
	local c = getClimateManager()
	local off = value == nil or value == "" or value == "off"
	local v = tonumber(value)
	if name ~= "reset" and not off and not v then error("climate " .. tostring(name) .. " needs a number, or off") end
	if name == "reset" then
		for _, index in ipairs({ FLOAT_PRECIPITATION, FLOAT_TEMPERATURE, FLOAT_FOG, FLOAT_WIND, FLOAT_CLOUDS }) do
			setAdmin(index, nil)
		end
		c:getClimateBool(BOOL_IS_SNOW):setEnableAdmin(false)
		audit("weather back to the game's own")
		return world()
	elseif name == "fog" then
		setAdmin(FLOAT_FOG, not off and v or nil)
	elseif name == "clouds" then
		setAdmin(FLOAT_CLOUDS, not off and v or nil)
	elseif name == "wind" then
		setAdmin(FLOAT_WIND, not off and v / c:getMaxWindspeedKph() or nil)
	elseif name == "temperature" then
		setAdmin(FLOAT_TEMPERATURE, not off and v or nil)
	elseif name == "snow" then
		-- snowfall: precipitation, falling as snow
		local snow = c:getClimateBool(BOOL_IS_SNOW)
		if off then
			setAdmin(FLOAT_PRECIPITATION, nil)
			snow:setEnableAdmin(false)
		else
			snow:setEnableAdmin(true)
			snow:setAdminValue(true)
			setAdmin(FLOAT_PRECIPITATION, v)
		end
	else
		error("unknown climate setting " .. tostring(name))
	end
	audit("weather: " .. name .. " " .. (off and "back to the game's own" or tostring(v)))
	return world()
end

-- ---- corpses (bridge v6) ----

local MAX_CORPSE_RADIUS = 100

-- a zombie's corpse only: a player's (their loot) and an animal's (butchering) stay
local function isZombieCorpse(body)
	if try(function() return body:isAnimal() end) then return false end
	if try(function() return body:isPlayer() end) then return false end
	return try(function() return body:isZombie() end) == true
end

-- removecorpses <x> <y> <radius>: the zombie corpses within radius squares, on every floor. As "Remove
-- bodies" of the debug Horde Manager (IsoGridSquare.removeCorpse, which on a server also tells the
-- clients nearby), with a real radius: in multiplayer that button clears the admin's whole loaded area.
-- The server only has the squares near players: elsewhere nothing is loaded, so nothing is found.
local function removeCorpses(x, y, radius)
	x, y, radius = tonumber(x), tonumber(y), tonumber(radius)
	if not x or not y or not radius then error("removecorpses needs x, y and a radius") end
	x, y = math.floor(x), math.floor(y)
	radius = math.max(1, math.min(MAX_CORPSE_RADIUS, math.floor(radius)))
	local cell = getCell()
	local removed, loaded = 0, 0
	for sx = x - radius, x + radius do
		for sy = y - radius, y + radius do
			if (sx - x) * (sx - x) + (sy - y) * (sy - y) <= radius * radius then
				local ground = cell:getGridSquare(sx, sy, 0)
				if ground then
					loaded = loaded + 1
					-- basements and upper floors: the levels this chunk has
					local chunk = try(function() return ground:getChunk() end)
					local minZ = chunk and try(function() return chunk:getMinLevel() end) or -1
					local maxZ = chunk and try(function() return chunk:getMaxLevel() end) or 7
					for z = minZ, maxZ do
						local sq = z == 0 and ground or cell:getGridSquare(sx, sy, z)
						if sq then
							local objects = sq:getStaticMovingObjects()
							-- backwards: removing takes the body out of this list
							for i = objects:size() - 1, 0, -1 do
								local body = objects:get(i)
								if instanceof(body, "IsoDeadBody") and isZombieCorpse(body) then
									sq:removeCorpse(body, false)
									removed = removed + 1
								end
							end
						end
					end
				end
			end
		end
	end
	audit("removed " .. removed .. " zombie corpses within " .. radius .. " squares of " .. x .. ", " .. y)
	return { removed = removed, loaded = loaded, radius = radius }
end

local actions = {
	removecorpses = removeCorpses,
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
	climate = setClimate,
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
