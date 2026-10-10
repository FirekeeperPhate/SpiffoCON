-- SpiffoCON Bridge: lets the SpiffoCON admin tool read what RCON can't (player positions,
-- inventories, vehicles, world state) and do a few admin actions (heal, remove items, repair,
-- refuel or remove vehicles, set the weather and the time, clean up an area, remove a safehouse,
-- put items on the ground, remove wrecks, set hair and beard, cure the zombie infection, replace the
-- containers that lost their liquid part) and keeps the deaths of players.
-- Server side only; it does nothing on clients.
--
-- Channel: files in the server's Zomboid/Lua folder, which SpiffoCON reads and writes over SFTP.
--   spiffocon_in.txt   written by SpiffoCON:  "SEQ <n>", one request per line (id<TAB>action<TAB>arg...), "END <n>"
--   spiffocon_out.txt  written here:          "SEQ <n>", one JSON reply per line, "END <n>"
-- A batch is handled once (each new SEQ); requests already in the file when the server
-- starts are ignored, so nothing runs twice after a restart.
if not isServer() then return end

local VERSION = 14
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

-- the character's own name, as made in the character creation ("John Smith"); bridge v10
local function characterName(p)
	local name = try(function()
		local d = p:getDescriptor()
		return ((d:getForename() or "") .. " " .. (d:getSurname() or "")):gsub("^%s+", ""):gsub("%s+$", "")
	end)
	return name ~= "" and name or nil
end

local function playerInfo(p)
	local info = {
		username = try(function() return p:getUsername() end),
		displayName = try(function() return p:getDisplayName() end),
		characterName = characterName(p),
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
		-- bridge v13: which one, for the vehicle window
		info.vehicleId = try(function() return vehicle:getId() end)
	end
	return info
end

-- how far "near" is for zombiesNear: about what a player sees around them
local NEAR_SQUARES = 30

local function listPlayers()
	local result = array()
	local players = getOnlinePlayers()
	for i = 0, players:size() - 1 do
		result[#result + 1] = playerInfo(players:get(i))
	end
	-- bridge v7: the zombies within NEAR_SQUARES of each player (any floor), in one pass over them
	try(function()
		local zombies = getCell():getZombieList()
		local near = {}
		for z = 0, zombies:size() - 1 do
			local zombie = zombies:get(z)
			local zx, zy = zombie:getX(), zombie:getY()
			for i, info in ipairs(result) do
				if info.x and (zx - info.x) * (zx - info.x) + (zy - info.y) * (zy - info.y) <= NEAR_SQUARES * NEAR_SQUARES then
					near[i] = (near[i] or 0) + 1
				end
			end
		end
		for i, info in ipairs(result) do info.zombiesNear = near[i] or 0 end
	end)
	return result
end

-- the label of a bag inside the container labelled parent: its name, and from the second bag of the
-- same name in that container on, its number ("Inventory > School Bag #2"), so removeitem can tell two
-- such bags apart (bridge v7). seen counts the bags of each name met so far in the container.
local function bagLabel(parent, name, seen)
	-- counted by name, not by type: the game has many bag types under one name (18 "Duffel Bag")
	seen[name] = (seen[name] or 0) + 1
	return parent .. " > " .. name .. (seen[name] > 1 and (" #" .. seen[name]) or "")
end

local function hundredths(v) return math.floor(v * 100 + 0.5) / 100 end

-- bridge v14: what there is to say about an item, for the menu on it. condition (0 to 1) for what wears out
-- and shows it (weapons, clothes, anything damaged), uses for what runs down (a battery, a lighter), fill for
-- what holds liquids (and water: whether water can be added), washable and dirty for clothes and weapons.
local function itemState(item)
	local s = {}
	local max = try(function() return item:getConditionMax() end)
	local condition = try(function() return item:getCondition() end)
	local weapon, clothing = instanceof(item, "HandWeapon"), instanceof(item, "Clothing")
	if max and max > 0 and condition and (condition < max or weapon or clothing) then
		s.condition = hundredths(condition / max)
	end
	if try(function() return item:IsDrainable() end) then
		local uses = try(function() return item:getMaxUses() end)
		if uses and uses > 0 then s.uses = try(function() return hundredths(item:getCurrentUses() / uses) end) end
	end
	local part = try(function() return item:getFluidContainer() end)
	if part then
		local capacity = try(function() return part:getCapacity() end)
		if capacity and capacity > 0 then s.fill = try(function() return hundredths(part:getAmount() / capacity) end) end
		-- whether water can go in (not into a can of petrol): Fill is offered only then
		s.water = try(function() return part:canAddFluid(Fluid.Water) end) == true
	end
	if clothing then
		s.washable = true
		s.dirty = try(function() return item:isDirty() or item:isBloody() end) == true
	elseif weapon then
		s.washable = true
		s.dirty = (try(function() return item:getBloodLevel() end) or 0) > 0
	end
	return s
end

-- a row is several items: the worst of each is what it says
local function addState(entry, s)
	for _, key in ipairs({ "condition", "uses", "fill" }) do
		if s[key] and (entry[key] == nil or s[key] < entry[key]) then entry[key] = s[key] end
	end
	if s.fill then entry.water = entry.water or s.water or false end
	if s.washable then
		entry.washable = true
		entry.dirty = entry.dirty or s.dirty or false
	end
end

-- items grouped by container and type: { container, fullType, name, count, equipped } and, since bridge v14,
-- their state (itemState)
local function addItems(result, container, label, player)
	local items = container:getItems()
	local grouped = {}
	local order = {}
	local bags = {}
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
		addState(entry, itemState(item))
		if player and (try(function() return player:isEquipped(item) end) or try(function() return item:isEquipped() end)) then
			entry.equipped = true
		end
		-- bags and other containers: list what is inside them too
		local inner = containerOf(item)
		if inner then
			addItems(result, inner, bagLabel(label, entry.name, bags), nil)
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

-- the name players see for a part of a vehicle ("Front Left Tire"), else its id
local function partName(part)
	local id = try(function() return part:getId() end) or "part"
	return try(function() return getTextOrNull("IGUI_VehiclePart" .. id) end) or id
end

-- the parts that take an item and have none: a wheel taken off, a window or a battery gone
-- (VehiclePart.isInventoryItemUninstalled)
local function isMissing(part)
	local types = part:getItemType()
	return types ~= nil and not types:isEmpty() and not part:getInventoryItem()
end

local function missingParts(v)
	local missing = {}
	try(function()
		for i = 0, v:getPartCount() - 1 do
			local part = v:getPartByIndex(i)
			if isMissing(part) then missing[#missing + 1] = part end
		end
	end)
	return missing
end

local function partNames(parts)
	local names = array()
	for _, part in ipairs(parts) do names[#names + 1] = partName(part) end
	return names
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
			-- bridge v13: the parts that are gone (a wheel, a window, the battery...)
			missing = partNames(missingParts(v)),
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

-- for what only makes sense on a living character (heal, cure, a new look): one who just died is still
-- listed online until they make a new character
local function requireLivingPlayer(username)
	local p = requirePlayer(username)
	if try(function() return p:isDead() end) then error(tostring(username) .. " is dead") end
	return p
end

-- as the health cheat "healthFullBody" in server/ClientCommands.lua
local function heal(username)
	local p = requireLivingPlayer(username)
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
	local bags = {}
	for i = 0, items:size() - 1 do
		local item = items:get(i)
		local itemType = item:getFullType()
		if itemType == fullType and (wanted == nil or wanted == label) then into[#into + 1] = item end
		-- addItems names a bag after the first item of its type in this container
		names[itemType] = names[itemType] or (try(function() return item:getDisplayName() end) or itemType)
		local inner = containerOf(item)
		if inner then collect(inner, bagLabel(label, names[itemType], bags), fullType, wanted, into) end
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

-- ---- things done to the items of a player (bridge v14) ----
-- Each changes the items on the server and tells their owner with InventoryItem.syncItemFields: the packet
-- carries the condition, the head condition, the sharpness, the times repaired, the uses, the dirt, blood and
-- wetness, the holes and patches of clothes, and the liquid inside. Each returns whether it applies to the item.
local ITEM_ACTIONS = {
	-- as new: the condition, the head of a tool, the edge of a blade, and no memory of earlier repairs; clothes
	-- with Clothing.fullyRestore, which also mends the holes, takes off the patches and washes them
	repair = function(item)
		local max = item:getConditionMax()
		if not max or max <= 0 then return false end
		if instanceof(item, "Clothing") then try(function() item:fullyRestore() end) end
		try(function() item:setBroken(false) end)
		item:setCondition(max)
		try(function() if item:hasHeadCondition() then item:setHeadCondition(item:getHeadConditionMax()) end end)
		try(function() item:applyMaxSharpness() end)
		try(function() item:setTimesRepaired(0) end)
		return true
	end,
	-- blood and dirt off, as washing does (ISWashClothing:complete), but dry
	clean = function(item)
		local clothing, bag = instanceof(item, "Clothing"), instanceof(item, "InventoryContainer")
		if not (clothing or bag or instanceof(item, "HandWeapon")) then return false end
		if clothing or bag then
			try(function()
				local parts = BloodClothingType.getCoveredParts(item:getBloodClothingType())
				if parts then
					for i = 0, parts:size() - 1 do
						item:setBlood(parts:get(i), 0)
						item:setDirt(parts:get(i), 0)
					end
				end
			end)
		end
		if clothing then
			try(function() item:setDirtiness(0) end)
			try(function() item:setWetness(0) end)
		end
		try(function() item:setBloodLevel(0) end)
		return true
	end,
	-- water to the top, where water can go (not into a can of petrol)
	fill = function(item)
		local part = item:getFluidContainer()
		if not part or not part:canAddFluid(Fluid.Water) then return false end
		local free = part:getFreeCapacity()
		if free > 0 then part:addFluid(Fluid.Water, free) end
		return true
	end,
	empty = function(item)
		local part = item:getFluidContainer()
		if not part then return false end
		part:Empty()
		return true
	end,
	-- a battery, a lighter, a spool of thread: full again
	recharge = function(item)
		if not item:IsDrainable() then return false end
		local uses = item:getMaxUses()
		if not uses or uses <= 0 then return false end
		item:setCurrentUses(uses)
		return true
	end,
}

-- itemaction <username> <action> <fullType> [container]: repair | clean | fill | empty | recharge, on the
-- items of that type listed under that container (as removeitem: every bag is a container of its own)
local function itemAction(username, action, fullType, container)
	local p = requirePlayer(username)
	local act = ITEM_ACTIONS[action]
	if not act then error("itemaction: unknown action " .. tostring(action)) end
	if not fullType or fullType == "" then error("itemaction needs the type of the item") end
	if container == "" then container = nil end
	local found = {}
	collect(p:getInventory(), "Inventory", fullType, container, found)
	if #found == 0 then error(username .. " has no " .. fullType .. (container and (" in " .. container) or "") .. " any more: refresh") end
	local done, skipped, visuals = 0, 0, false
	for _, item in ipairs(found) do
		if try(act, item) == true then
			done = done + 1
			item:syncItemFields()
			if instanceof(item, "Clothing") then visuals = true end
		else
			skipped = skipped + 1
		end
	end
	-- what the others see of the clothes the player wears (as ISWashClothing:complete)
	if visuals then try(function() syncVisuals(p) end) end
	if done > 0 then audit(action .. " " .. done .. " x " .. fullType .. " of " .. username) end
	return { done = done, skipped = skipped }
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

-- Every part whole, and those that are gone put back (bridge v13).
-- The game's own repair (Commands.repair: BaseVehicle.repair, each VehiclePart.repair) is meant to do both: a
-- part without its item gets a new one, every condition goes to 100, tyres and tank are filled, each change
-- sent to the players. What it leaves without an item is then installed the way a mechanic's install ends
-- (ISInstallVehiclePart:complete: the item, the part's install.complete, transmitPartItem) and repaired.
-- The reply names what was missing, what had to be installed here, and what could not be.
-- a part the game's repair left without its item: installed the way a mechanic's install ends
local function installPart(v, part)
	try(function()
		local item = VehicleUtils.createPartInventoryItem(part)
		if not item then
			item = instanceItem(part:getItemType():get(0))
			part:setInventoryItem(item)
		end
		local install = part:getTable("install")
		if install and install.complete then VehicleUtils.callLua(install.complete, v, part) end
		v:transmitPartItem(part)
		-- new parts come worn: to 100 like the rest, a tyre inflated
		part:repair()
	end)
	return not isMissing(part)
end

local function repairFully(v)
	local before = missingParts(v)
	local beforeNames = partNames(before)
	v:repair()
	local installed = array()
	for _, part in ipairs(missingParts(v)) do
		local name = partName(part)
		if installPart(v, part) then installed[#installed + 1] = name end
	end
	local still = partNames(missingParts(v))
	audit("repaired " .. vehicleName(v) .. (#beforeNames > 0 and (", " .. #beforeNames .. " missing parts put back") or "")
		.. (#still > 0 and (", " .. #still .. " still missing") or ""))
	return { repaired = true, missing = beforeNames, installed = installed, stillMissing = still }
end

-- as Commands.repair in server/Vehicles/VehicleCommands.lua, then what it left out
local function repairVehicle(id, script)
	return repairFully(requireVehicle(id, script))
end

-- repairvehicleof <username>: the vehicle that player is in now (bridge v13)
local function repairVehicleOf(username)
	local p = requirePlayer(username)
	local v = p:getVehicle()
	if not v then error(tostring(username) .. " is not in a vehicle") end
	local result = repairFully(v)
	result.vehicle = try(function() return v:getScript():getFullName() end)
	result.id = try(function() return v:getId() end)
	return result
end

-- repairpart <id> <script> <part id>: one part made whole, or put back when it is gone (bridge v13).
-- As the mechanics window's own "Repair Part" (Commands.repairPart: VehiclePart.repair), then what it left out.
local function repairPart(id, script, partId)
	local v = requireVehicle(id, script)
	local part = v:getPartById(tostring(partId))
	if not part then error(vehicleName(v) .. " has no part " .. tostring(partId)) end
	local name = partName(part)
	local wasMissing = isMissing(part)
	local before = try(function() return part:getCondition() end)
	part:repair()
	local installed = false
	if isMissing(part) then installed = installPart(v, part) end
	audit((wasMissing and "put back " or "repaired ") .. name .. " of " .. vehicleName(v))
	return {
		part = name, wasMissing = wasMissing, installed = installed, missing = isMissing(part),
		before = before, condition = try(function() return part:getCondition() end),
	}
end

-- the name players see for a vehicle ("Chevalier Dart"), as the mechanics window writes it
local function vehicleDisplayName(v)
	return try(function()
		local script = v:getScript()
		local car = script:getCarModelName() or script:getName()
		local name = getTextOrNull("IGUI_VehicleName" .. car)
		if string.match(script:getName(), "Burnt") then
			local unburnt = (string.gsub(script:getName(), "Burnt", ""))
			name = getTextOrNull("IGUI_VehicleName" .. unburnt) or name
			if name then name = getText("IGUI_VehicleNameBurntCar", name) end
		end
		return name
	end)
end

-- a part as the mechanics window shows it: condition, what is in it, missing or not
local function partDetails(part)
	local category = try(function() return part:getCategory() end) or "Other"
	local item = try(function() return part:getInventoryItem() end)
	local info = {
		id = try(function() return part:getId() end),
		name = partName(part),
		category = category,
		categoryName = try(function() return getTextOrNull("IGUI_VehiclePartCat" .. category) end) or category,
		condition = try(function() return part:getCondition() end),
		missing = try(function() return isMissing(part) end) or false,
		-- false: a part of the body itself (the engine, a seat frame), nothing to install
		takesItem = try(function() local types = part:getItemType() return types ~= nil and not types:isEmpty() end) or false,
		item = item and try(function() return item:getDisplayName() end) or nil,
	}
	-- the air of a tyre, the fuel of the tank
	local content = try(function() return part:isContainer() and part:getContainerContentType() end)
	if content then
		info.content = content
		info.amount = try(function() return part:getContainerContentAmount() end)
		info.capacity = try(function() return part:getContainerCapacity() end)
	end
	if info.id == "Battery" and item then
		info.charge = try(function() return item:getCurrentUsesFloat() end)
	end
	local door = try(function() return part:getDoor() end)
	if door then
		info.open = try(function() return door:isOpen() end)
		info.locked = try(function() return door:isLocked() end)
	end
	local window = try(function() return part:getWindow() end)
	if window then info.open = try(function() return window:isOpen() end) end
	return info
end

-- vehicle <id> <script>: one vehicle part by part, as the mechanics window of the game (bridge v13)
local function vehicleDetails(id, script)
	local v = requireVehicle(id, script)
	local parts = array()
	local total, count = 0, 0
	for i = 0, v:getPartCount() - 1 do
		local info = partDetails(v:getPartByIndex(i))
		-- the overall condition counts every part, one that is gone as 0 (recalculGeneralCondition)
		total = total + (info.missing and 0 or (info.condition or 0))
		count = count + 1
		if info.category ~= "nodisplay" then parts[#parts + 1] = info end
	end
	local seats = array()
	try(function()
		for seat = 0, v:getMaxPassengers() - 1 do
			local who = v:getCharacter(seat)
			local username = who and try(function() return who:getUsername() end)
			if username then seats[#seats + 1] = { seat = seat, username = username } end
		end
	end)
	local kind = try(function() return v:getScript():getMechanicType() end)
	return {
		id = try(function() return v:getId() end),
		script = try(function() return v:getScript():getFullName() end),
		name = vehicleDisplayName(v),
		x = try(function() return math.floor(v:getX()) end),
		y = try(function() return math.floor(v:getY()) end),
		z = try(function() return math.floor(v:getZ()) end),
		kind = kind and try(function() return getTextOrNull("IGUI_VehicleType_" .. kind) end) or nil,
		condition = count > 0 and math.floor(total / count * 100 + 0.5) / 100 or nil,
		mass = try(function() return v:getMass() end),
		enginePower = try(function() return v:getEnginePower() / 10 end),
		engineQuality = try(function() return v:getEngineQuality() end),
		engineLoudness = try(function() return v:getEngineLoudness() end),
		engineRunning = try(function() return v:isEngineRunning() end),
		rust = try(function() return v:getRust() end),
		hotwired = try(function() return v:isHotwired() end),
		keyInIgnition = try(function() return v:isKeysInIgnition() end),
		seats = seats,
		parts = parts,
	}
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

-- ---- areas: zombie corpses (bridge v6), items on the ground and fires (bridge v7) ----
-- Within a radius of a spot, on every floor. The server only has the squares near players: elsewhere
-- nothing is loaded, so nothing is found ("loaded" in the reply counts the ground squares it had).

local MAX_AREA_RADIUS = 100

-- x, y, radius as numbers; with a username, where that player is now (SpiffoCON's x and y are from its
-- last refresh, seconds old: far behind someone driving)
local function areaOf(action, x, y, radius, username)
	if username and username ~= "" then
		local p = requirePlayer(username)
		x, y = p:getX(), p:getY()
	end
	x, y, radius = tonumber(x), tonumber(y), tonumber(radius)
	if not x or not y or not radius then error(action .. " needs x, y and a radius") end
	-- written this way round so that "nan" fails too; a number too big to count on would loop for ever
	if not (math.abs(x) < 1e7 and math.abs(y) < 1e7 and radius >= 0) then error(action .. ": x, y or the radius is out of range") end
	return math.floor(x), math.floor(y), math.max(1, math.min(MAX_AREA_RADIUS, math.floor(radius)))
end

-- calls visit(square) for every loaded square of the circle, basements and upper floors included;
-- returns how many ground squares were loaded
local function eachSquare(x, y, radius, visit)
	local cell = getCell()
	local loaded = 0
	-- the levels each chunk has, asked once per chunk (8 x 8 squares in B42), not once per square
	local levels = {}
	for sx = x - radius, x + radius do
		for sy = y - radius, y + radius do
			if (sx - x) * (sx - x) + (sy - y) * (sy - y) <= radius * radius then
				local ground = cell:getGridSquare(sx, sy, 0)
				if ground then
					loaded = loaded + 1
					local key = math.floor(sx / 8) * 100000 + math.floor(sy / 8)
					local range = levels[key]
					if not range then
						range = try(function()
							local chunk = ground:getChunk()
							return { chunk:getMinLevel(), chunk:getMaxLevel() }
						end) or { -1, 7 }
						levels[key] = range
					end
					local minZ, maxZ = range[1], range[2]
					for z = minZ, maxZ do
						local sq = z == 0 and ground or cell:getGridSquare(sx, sy, z)
						if sq then visit(sq) end
					end
				end
			end
		end
	end
	return loaded
end

-- the corpse of a zombie only: those of players (their loot) and of animals (butchering) stay
local function isZombieCorpse(body)
	if try(function() return body:isAnimal() end) then return false end
	if try(function() return body:isPlayer() end) then return false end
	return try(function() return body:isZombie() end) == true
end

-- removecorpses <x> <y> <radius> [username]: as "Remove bodies" of the debug Horde Manager
-- (IsoGridSquare.removeCorpse, which on a server also tells the clients nearby), with a real radius:
-- in multiplayer that button clears the whole loaded area of the admin.
local function removeCorpses(x, y, radius, username)
	x, y, radius = areaOf("removecorpses", x, y, radius, username)
	local removed = 0
	local loaded = eachSquare(x, y, radius, function(sq)
		local objects = sq:getStaticMovingObjects()
		-- backwards: removing takes the body out of this list
		for i = objects:size() - 1, 0, -1 do
			local body = objects:get(i)
			if instanceof(body, "IsoDeadBody") and isZombieCorpse(body) then
				sq:removeCorpse(body, false)
				removed = removed + 1
			end
		end
	end)
	audit("removed " .. removed .. " zombie corpses within " .. radius .. " squares of " .. x .. ", " .. y)
	return { removed = removed, loaded = loaded, radius = radius }
end

-- removegrounditems <x> <y> <radius> <apply: 0 | 1> <safehouses: 0 | 1> [username]: the items lying on
-- the floor, as the debug "Remove items" tool (IsoGridSquare.transmitRemoveItemFromSquare, which on a
-- server tells the clients). Not what is inside containers, and not what players put on a table, a
-- shelf or a counter: those items have a height (getOffZ) and stay, counted apart. With apply 0 it only
-- counts, so SpiffoCON can say how many before asking; items inside a safehouse stay unless safehouses
-- is 1, and are counted apart too.
local function removeGroundItems(x, y, radius, apply, safehouses, username)
	x, y, radius = areaOf("removegrounditems", x, y, radius, username)
	apply = apply == "1"
	safehouses = safehouses == "1"
	local found, removed, kept, raised = 0, 0, 0, 0
	local loaded = eachSquare(x, y, radius, function(sq)
		local objects = sq:getWorldObjects()
		local count = objects:size()
		if count == 0 then return end
		if not safehouses and try(function() return SafeHouse.getSafeHouse(sq) end) then
			kept = kept + count
			return
		end
		-- backwards: removing takes the object out of this list
		for i = count - 1, 0, -1 do
			local object = objects:get(i)
			-- on the floor only when the game says so: a height that can't be read is not a reason to delete
			local height = try(function() return object:getOffZ() end)
			if height and height < 0.01 then
				found = found + 1
				if apply then
					sq:transmitRemoveItemFromSquare(object)
					-- as the tool does after it: nothing left behind on the square the server has
					try(function() object:removeFromWorld() end)
					try(function() object:removeFromSquare() end)
					try(function() object:setSquare(nil) end)
					removed = removed + 1
				end
			else
				raised = raised + 1
			end
		end
	end)
	if apply then audit("removed " .. removed .. " items on the ground within " .. radius .. " squares of " .. x .. ", " .. y) end
	return { found = found, removed = removed, inSafehouses = kept, onFurniture = raised, loaded = loaded, radius = radius }
end

-- ---- containers that lost their liquid part (bridge v14) ----
-- An item that holds liquids (a bucket, a bottle, a pot) does it with a FluidContainer component. Put down
-- in the world, the component moves from the item to the object on the square (the constructor of
-- IsoWorldInventoryObject; removeFromSquare gives it back) and is saved with that object, apart from the
-- item. After a server crash some come back without it, on either of the two. The item is then a plain
-- "Bucket" for good (InventoryItem.getName falls back to the name of the item; a whole one is "Empty
-- Bucket" or "Bucket of Water"), and the Fill menu, which lists the items with the component, leaves it out.

-- whether the game's script gives this type of item a FluidContainer, asked once per type
local holdsFluid = {}
local function shouldHoldFluid(item)
	local fullType = item:getFullType()
	local known = holdsFluid[fullType]
	if known == nil then
		known = try(function() return item:getScriptItem():getComponentScriptFor(ComponentType.FluidContainer) ~= nil end) == true
		holdsFluid[fullType] = known
	end
	return known
end

-- an item in an inventory or a container: it should hold liquids and can not
local function lostFluidPart(item)
	return item:getFluidContainer() == nil and shouldHoldFluid(item)
end

-- calls visit(item, container) for each such item of a container, the bags in it included
local function eachBrokenIn(container, visit)
	local items = container:getItems()
	-- backwards: a replaced item leaves this list, and the new one goes to its end
	for i = items:size() - 1, 0, -1 do
		local item = items:get(i)
		local inner = containerOf(item)
		if inner then eachBrokenIn(inner, visit) end
		if lostFluidPart(item) then visit(item, container) end
	end
end

-- a new item of the same type, whole: nil when the game makes none, or makes it without the component too
local function wholeOne(item)
	local new = try(function() return instanceItem(item:getFullType()) end)
	if not new or new:getFluidContainer() == nil then return nil end
	-- empty, as the one it replaces: some types are made with their drink in them (a bottle of water)
	try(function() new:getFluidContainer():Empty() end)
	return new
end

-- in a container: out and in, each told to the clients (as removeItem above and vehicleKey below)
local function replaceInContainer(item, container, player)
	local new = wholeOne(item)
	if not new then return false end
	if player then try(function() player:removeFromHands(item) end) end
	container:Remove(item)
	sendRemoveItemFromContainer(container, item)
	container:AddItem(new)
	sendAddItemToContainer(container, new)
	return true
end

-- on a square: the old object out as "Remove items" does, the new one where it was and turned the same way,
-- put down as a player puts an item down (ISDropWorldItemAction:complete)
local function replaceOnSquare(sq, object)
	local item = object:getItem()
	local new = wholeOne(item)
	if not new then return false end
	local ox, oy, oz = object:getOffX(), object:getOffY(), object:getOffZ()
	local rx = try(function() return item:getWorldXRotation() end)
	local ry = try(function() return item:getWorldYRotation() end)
	local rz = try(function() return item:getWorldZRotation() end)
	local extended = try(function() return object:isExtendedPlacement() end)
	local keep = try(function() return object:isIgnoreRemoveSandbox() end)
	sq:transmitRemoveItemFromSquare(object)
	try(function() object:removeFromWorld() end)
	try(function() object:removeFromSquare() end)
	try(function() object:setSquare(nil) end)
	local placed = sq:AddWorldInventoryItem(new, ox, oy, oz, false)
	if not placed then return false end
	try(function()
		if rx then placed:setWorldXRotation(rx) end
		if ry then placed:setWorldYRotation(ry) end
		if rz then placed:setWorldZRotation(rz) end
	end)
	local now = placed:getWorldItem()
	if now then
		-- a thing put down on purpose is not litter for the sandbox's item removal
		try(function() now:setIgnoreRemoveSandbox(keep ~= false) end)
		try(function() now:setExtendedPlacement(extended == true) end)
		now:transmitCompleteItemToClients()
	end
	return true
end

-- fixfluids <apply: 0 | 1> <radius> [username]: the containers that lost their liquid part, in the
-- inventories of the players online (of that player only, with a username), on the squares within the radius
-- of each of them (on the ground or on furniture), and inside the crates, shelves and fridges there. With
-- apply 1 each is replaced by a new, empty one of the same type in the same place; one that is worn or
-- attached is left and counted. With apply 0 it only counts, so SpiffoCON can say what before asking.
local function fixFluids(apply, radius, username)
	apply = apply == "1"
	radius = tonumber(radius)
	if not radius or not (radius >= 0) then error("fixfluids needs a radius") end
	radius = math.max(1, math.min(MAX_AREA_RADIUS, math.floor(radius)))
	local players = {}
	if username and username ~= "" then
		players[1] = requirePlayer(username)
	else
		local online = getOnlinePlayers()
		for i = 0, online:size() - 1 do players[#players + 1] = online:get(i) end
	end

	local found, fixed, skipped, failed = 0, 0, 0, 0
	local inInventories, onSquares, inContainers = 0, 0, 0
	-- what was found, by where and type: { where, fullType, name, count }
	local list, rows = array(), {}
	local function note(where, item)
		found = found + 1
		local fullType = item:getFullType()
		local key = where .. "\t" .. fullType
		local row = rows[key]
		if not row then
			row = { where = where, fullType = fullType, name = try(function() return item:getDisplayName() end) or fullType, count = 0 }
			rows[key] = row
			list[#list + 1] = row
		end
		row.count = row.count + 1
	end
	local function done(ok)
		if ok then fixed = fixed + 1 else failed = failed + 1 end
	end

	for _, p in ipairs(players) do
		local name = p:getUsername()
		eachBrokenIn(p:getInventory(), function(item, container)
			note(name, item)
			inInventories = inInventories + 1
			-- counted also when only counting: SpiffoCON says it before asking
			if isWornOrAttached(p, item) then
				skipped = skipped + 1
			elseif apply then
				done(try(replaceInContainer, item, container, p) == true)
			end
		end)
	end

	-- the areas of two players next to each other overlap: each square once
	local seen = {}
	local loaded = 0
	for _, p in ipairs(players) do
		loaded = loaded + eachSquare(math.floor(p:getX()), math.floor(p:getY()), radius, function(sq)
			local key = sq:getX() .. "," .. sq:getY() .. "," .. sq:getZ()
			if seen[key] then return end
			seen[key] = true
			local objects = sq:getWorldObjects()
			-- backwards: replacing takes the object out of this list
			for i = objects:size() - 1, 0, -1 do
				local object = objects:get(i)
				local item = try(function() return object:getItem() end)
				-- put down, the component is on the object: gone only when neither has it (and only when the
				-- game answered: a call that fails is not "it has none")
				local asked, part = pcall(function() return object:getFluidContainer() end)
				if item and asked and part == nil and lostFluidPart(item) then
					note("", item)
					onSquares = onSquares + 1
					if apply then done(try(replaceOnSquare, sq, object) == true) end
				end
			end
			local things = sq:getObjects()
			for i = 0, things:size() - 1 do
				local thing = things:get(i)
				local count = try(function() return thing:getContainerCount() end) or 0
				for c = 0, count - 1 do
					local container = try(function() return thing:getContainerByIndex(c) end)
					if container then
						eachBrokenIn(container, function(item, holder)
							note("", item)
							inContainers = inContainers + 1
							if apply then done(try(replaceInContainer, item, holder, nil) == true) end
						end)
					end
				end
			end
		end)
	end

	if apply then
		audit("replaced " .. fixed .. " containers that had lost their liquid part"
			.. ((username and username ~= "") and (", " .. username .. " and " .. radius .. " squares around")
				or (", the players online and " .. radius .. " squares around each")))
	end
	return {
		found = found, fixed = fixed, skipped = skipped, failed = failed,
		inInventories = inInventories, onSquares = onSquares, inContainers = inContainers,
		players = #players, loaded = loaded, radius = radius, items = list,
	}
end

-- a fire to put out: not the flame of a lit campfire or the like, which is an IsoFire too but a
-- permanent one (as FireFighting.isSquareToExtinguish in the game's own Lua)
local function isBurning(sq)
	if not sq:haveFire() then return false end
	local objects = sq:getObjects()
	for i = 0, objects:size() - 1 do
		local object = objects:get(i)
		if instanceof(object, "IsoFire") and not object:isPermanent() then return true end
	end
	return false
end

-- stopfires <x> <y> <radius> [username]: stopFire(square) of the game, which on a server puts the
-- fire out and tells the clients nearby
local function stopFires(x, y, radius, username)
	x, y, radius = areaOf("stopfires", x, y, radius, username)
	local stopped = 0
	local loaded = eachSquare(x, y, radius, function(sq)
		if isBurning(sq) then
			stopFire(sq)
			stopped = stopped + 1
		end
	end)
	audit("put out " .. stopped .. " burning squares within " .. radius .. " squares of " .. x .. ", " .. y)
	return { stopped = stopped, loaded = loaded, radius = radius }
end

-- ---- safehouses (bridge v7) ----

local function listSafehouses()
	local result = array()
	local list = SafeHouse.getSafehouseList()
	for i = 0, list:size() - 1 do
		local s = list:get(i)
		local members = array()
		local owner = try(function() return s:getOwner() end)
		local players = try(function() return s:getPlayers() end)
		if players then
			for m = 0, players:size() - 1 do
				-- the game keeps the owner in this list too (SafeHouse's constructor adds them)
				local name = tostring(players:get(m))
				if name ~= owner then members[#members + 1] = name end
			end
		end
		result[#result + 1] = {
			-- as a string: an id for the remove action, not a number to compute with
			id = try(function() return tostring(s:getOnlineID()) end),
			title = try(function() return s:getTitle() end),
			owner = try(function() return s:getOwner() end),
			members = members,
			x = try(function() return s:getX() end),
			y = try(function() return s:getY() end),
			w = try(function() return s:getW() end),
			h = try(function() return s:getH() end),
			-- milliseconds since 1970, as a string (a Java long through Lua loses digits as a number)
			lastVisited = try(function() return string.format("%.0f", s:getLastVisited()) end),
			online = try(function() return s:getPlayerConnected() end),
		}
	end
	return result
end

-- removesafehouse <id> <owner>: the owner is checked, so a safehouse that got this id after the list
-- was read is not removed in its place. The way the server itself removes one and tells every client
-- is SafeHouse.hitPoint (the safehouse-war hit that takes the last hit point): the hit points are set
-- one short of the WarSafehouseHitPoints option first. What is inside is not touched.
local function removeSafehouse(id, owner)
	id = tonumber(id)
	if not id then error("removesafehouse needs an id") end
	local safe = SafeHouse.getSafeHouse(id)
	if not safe then error("no safehouse with id " .. tostring(id) .. ": refresh the list") end
	if owner and owner ~= "" and safe:getOwner() ~= owner then
		error("safehouse " .. tostring(id) .. " now belongs to " .. tostring(safe:getOwner()) .. ", not " .. owner .. ": refresh the list")
	end
	local name = tostring(safe:getTitle()) .. " of " .. tostring(safe:getOwner()) .. " at " .. tostring(safe:getX()) .. ", " .. tostring(safe:getY())
	-- hitPoint removes it when its hit points plus one equal the option, whatever the option is (0 too)
	local last = try(function() return getServerOptions():getInteger("WarSafehouseHitPoints") end)
	if last then
		safe:setHitPoints(last - 1)
		SafeHouse.hitPoint(id)
	end
	if SafeHouse.getSafehouseList():contains(safe) then
		-- the option could not be read: off the list of the server at least (clients learn at their next login)
		SafeHouse.removeSafeHouse(safe)
		audit("removed safehouse " .. name .. " (clients are told at their next login)")
		return { removed = true, synced = false }
	end
	audit("removed safehouse " .. name)
	return { removed = true, synced = true }
end

-- ---- the sheet of a player (bridge v7) ----

local STATS = { "HUNGER", "THIRST", "FATIGUE", "ENDURANCE", "STRESS", "PANIC", "BOREDOM", "UNHAPPINESS", "PAIN", "INTOXICATION", "SICKNESS" }

-- playerdetails <username>: traits, skills and condition, as the server has them
-- each part of the body: its health (0 to 100) and what is wrong with it, checked as the game's health
-- panel does (ISHealthPanel). Every part is listed, the sound ones too (the app shows those with something to say)
local PART_CONDITIONS = {
	{ "bitten", function(part) return part:bitten() end },
	{ "scratched", function(part) return part:scratched() end },
	{ "laceration", function(part) return part:isCut() end },
	{ "deep wound", function(part) return part:deepWounded() end },
	{ "bleeding", function(part) return part:bleeding() end },
	{ "fracture", function(part) return part:getFractureTime() > 0 end },
	{ "burn", function(part) return part:getBurnTime() > 0 end },
	{ "glass", function(part) return part:haveGlass() end },
	{ "bullet", function(part) return part:haveBullet() end },
	{ "infected wound", function(part) return part:isInfectedWound() end },
	{ "zombie infection", function(part) return part:IsInfected() end },
	{ "stitched", function(part) return part:stitched() end },
	{ "splint", function(part) return part:getSplintFactor() > 0 end },
	{ "bandaged", function(part) return part:bandaged() and part:getBandageLife() > 0 end },
	{ "dirty bandage", function(part) return part:bandaged() and part:getBandageLife() <= 0 end },
}

local function bodyParts(p)
	local result = array()
	try(function()
		local parts = p:getBodyDamage():getBodyParts()
		for i = 0, parts:size() - 1 do
			local part = parts:get(i)
			local conditions = array()
			for _, c in ipairs(PART_CONDITIONS) do
				if try(c[2], part) then conditions[#conditions + 1] = c[1] end
			end
			result[#result + 1] = {
				name = try(function() return BodyPartType.getDisplayName(part:getType()) end)
					or try(function() return BodyPartType.ToString(part:getType()) end) or tostring(i),
				health = try(function() return math.floor(part:getHealth() + 0.5) end),
				conditions = conditions,
			}
		end
	end)
	return result
end

-- an item worn, held or attached: its name and how worn out it is (0 to 1; nil for what has no condition)
local function equipmentItem(kind, slot, item)
	return {
		kind = kind,
		slot = slot,
		name = try(function() return item:getDisplayName() end) or try(function() return item:getFullType() end),
		fullType = try(function() return item:getFullType() end),
		condition = try(function()
			local max = item:getConditionMax()
			if not max or max <= 0 then return nil end
			return round(item:getCondition() / max, 0.01)
		end),
	}
end

-- what the character wears (by body location), holds (hands) and has attached (belt, back, holsters)
local function equipment(p)
	local result = array()
	try(function()
		local primary, secondary = p:getPrimaryHandItem(), p:getSecondaryHandItem()
		if primary and primary == secondary then
			result[#result + 1] = equipmentItem("hand", "Both hands", primary)
		else
			if primary then result[#result + 1] = equipmentItem("hand", "Primary hand", primary) end
			if secondary then result[#result + 1] = equipmentItem("hand", "Secondary hand", secondary) end
		end
	end)
	try(function()
		local worn = p:getWornItems()
		for i = 0, worn:size() - 1 do
			local w = worn:get(i)
			local location = w:getLocation()
			-- the game's own name for the place when it has one, else its id ("base:jacket")
			local slot = try(function() return getTextOrNull(location:getTranslationName()) end) or tostring(location)
			result[#result + 1] = equipmentItem("worn", slot, w:getItem())
		end
	end)
	try(function()
		local attached = p:getAttachedItems()
		for i = 0, attached:size() - 1 do
			local a = attached:get(i)
			result[#result + 1] = equipmentItem("attached", tostring(a:getLocation()), a:getItem())
		end
	end)
	return result
end

local function playerDetails(username)
	local p = requirePlayer(username)
	local info = playerInfo(p)

	local traits = array()
	try(function()
		local known = p:getCharacterTraits():getKnownTraits()
		for i = 0, known:size() - 1 do
			local trait = known:get(i)
			local def = try(function() return CharacterTraitDefinition.getCharacterTraitDefinition(trait) end)
			traits[#traits + 1] = (def and (try(function() return def:getLabel() end) or try(function() return def:getUIName() end))) or tostring(trait)
		end
	end)
	info.traits = traits

	local skills = array()
	try(function()
		for i = 0, PerkFactory.PerkList:size() - 1 do
			local perk = PerkFactory.PerkList:get(i)
			local parent = perk:getParent()
			-- as the skills panel of the game: the categories themselves are not skills
			if parent ~= Perks.None then
				local level = try(function() return p:getPerkLevel(perk) end)
				-- bridge v12: the id RCON's addxp takes ("Woodwork" for Carpentry), and the experience as the
				-- game's skill bar counts it (ISSkillProgressBar): gained within the level, and what the next
				-- level takes (none after the last)
				local xp = try(function() return p:getXp():getXP(perk) end)
				local before = level and try(function() return perk:getTotalXpForLevel(level) end)
				local next = level and level < 10 and try(function() return perk:getXpForLevel(level + 1) end) or nil
				skills[#skills + 1] = {
					name = try(function() return perk:getName() end) or tostring(perk),
					category = try(function() return parent:getName() end) or tostring(parent),
					level = level,
					id = try(function() return perk:getId() end),
					xp = xp and round(xp, 0.01),
					levelXp = xp and before and round(math.max(0, xp - before), 0.01),
					nextXp = next and next > 0 and round(next, 0.01) or nil,
				}
			end
		end
	end)
	info.skills = skills

	-- each as a fraction of its own range (the game keeps some 0 to 1, others 0 to 100); endurance: 1 is rested
	local stats = {}
	for _, name in ipairs(STATS) do
		stats[name:lower()] = try(function()
			local stat = CharacterStat[name]
			local min, max = stat:getMinimumValue(), stat:getMaximumValue()
			return round((p:getStats():get(stat) - min) / (max - min), 0.01)
		end)
	end
	info.stats = stats

	local body = try(function() return p:getBodyDamage() end)
	if body then
		info.infected = try(function() return body:IsInfected() end)
		info.bitten = try(function() return body:getNumPartsBitten() end)
		info.onFire = try(function() return body:IsOnFire() end)
	end

	-- bridge v11: the body part by part, what is worn, held and attached, the weight carried
	info.parts = bodyParts(p)
	info.equipment = equipment(p)
	info.weight = try(function() return round(p:getInventory():getCapacityWeight(), 0.1) end)
	info.maxWeight = try(function() return p:getMaxWeight() end)
	info.asleep = try(function() return p:isAsleep() end)
	return info
end

-- ---- time and keys (bridge v7) ----

-- settime <hour>: 0 to 24 with decimals (7.5 = 07:30). The clock skips forward to the next time it is
-- that hour, as if the hours in between had passed: the game has no way back. GameTime counts the age
-- of the world as its nights, each beginning at 7:00, plus the time of day, so a clock set back would
-- make the world younger; and it turns the calendar itself when the time of day reaches 24. The
-- server sends its clock to the clients every ten seconds.

-- a night passed by the skip that is counted only once GameTime.update has turned the day (see poll):
-- counted before, the world would be a day too old for the tick in between
local nightToCount = false

local function setTime(hour)
	hour = tonumber(hour)
	-- written this way round so that "nan" fails too
	if not hour or not (hour >= 0 and hour < 24) then error("settime needs an hour from 0 to 24") end
	-- to the minute: SpiffoCON sends the hour with decimals (8:20 is 8.333333)
	local minutes = math.min(1439, math.floor(hour * 60 + 0.5))
	hour = minutes / 60
	-- at 7:00 sharp the game itself would count the new night again on its next tick: a moment after it
	if minutes == 420 then hour = 7.001 end
	local t = getGameTime()
	local now = t:getTimeOfDay()
	if now >= 24 or nightToCount then error("the clock is still turning from the last change: try again in a moment") end
	local skipped = hour - now
	if skipped <= 0 then skipped = skipped + 24 end
	-- a time a few minutes behind the clock would skip a whole day (and the clients, who only see their
	-- clock go on, would not turn their calendar): surely not what was meant
	if skipped > 23.5 then
		local nowMinutes = math.floor(now * 60 + 0.5)
		error(string.format("it is %02d:%02d in game: that would skip almost a whole day. Choose a time at least half an hour from now",
			math.floor(nowMinutes / 60), nowMinutes % 60))
	end
	local tomorrow = now + skipped >= 24
	-- the night that begins at 7:00, if the skip passes it (the game counts it only as its own clock ticks past)
	if (now <= 7 and now + skipped > 7) or (now > 7 and now + skipped > 31) then
		if now > 7 then
			nightToCount = true
		else
			t:setNightsSurvived(t:getNightsSurvived() + 1)
		end
	end
	-- past midnight: 24 more, and GameTime.update turns the calendar (and fires EveryDays) on its next tick
	t:setTimeOfDay(tomorrow and hour + 24 or hour)
	audit("time of day set to " .. string.format("%02d:%02d", math.floor(minutes / 60), minutes % 60)
		.. (tomorrow and " of the next day" or "") .. ": " .. string.format("%.1f", skipped) .. " hours skipped")
	local w = world()
	w.hour = math.floor(minutes / 60)
	w.minute = minutes % 60
	w.skippedHours = round(skipped, 0.01)
	return w
end

-- vehiclekey <id> <script> <username>: as Commands.getKey in server/Vehicles/VehicleCommands.lua
local function vehicleKey(id, script, username)
	local v = requireVehicle(id, script)
	local p = requirePlayer(username)
	local key = v:createVehicleKey()
	if not key then error(vehicleName(v) .. " gave no key") end
	p:getInventory():AddItem(key)
	sendAddItemToContainer(p:getInventory(), key)
	audit("gave " .. username .. " the key of " .. vehicleName(v))
	return { given = true, name = try(function() return key:getDisplayName() end) }
end

-- ---- items put on the ground (bridge v8) ----

-- at most this many objects in one request: each one is an object of its own on the ground
local MAX_SPAWN = 500

-- spawnitems <x> <y> <z> <Type=count,Type=count,...>: the items on the floor of that square, as the
-- game's own server code drops things (IsoGridSquare.AddWorldInventoryItem with a type name: on a
-- server it sends each new object to the clients). Spread a little over the square, not in one pile.
-- A type the game does not know is skipped and named in the reply. The square must be loaded, that
-- is near a player.
local function spawnItems(x, y, z, list)
	x, y, z = tonumber(x), tonumber(y), tonumber(z) or 0
	-- written this way round so that "nan" fails too
	if not (x and y and math.abs(x) < 1e7 and math.abs(y) < 1e7 and math.abs(z) < 64) then error("spawnitems needs x, y and z") end
	if not list or list == "" then error("spawnitems needs the items: Type=count,Type=count") end
	local wanted, total = {}, 0
	for entry in (list .. ","):gmatch("([^,]*),") do
		local fullType, count = entry:match("^%s*([^=%s]+)%s*=%s*(%d+)%s*$")
		count = tonumber(count)
		if not fullType or not count or count < 1 then error("spawnitems: \"" .. entry .. "\" is not Type=count") end
		wanted[#wanted + 1] = { fullType = fullType, count = count }
		total = total + count
	end
	if total > MAX_SPAWN then error("spawnitems: " .. total .. " objects at once, more than " .. MAX_SPAWN) end
	local sq = getCell():getGridSquare(math.floor(x), math.floor(y), math.floor(z))
	if not sq then error("the square " .. math.floor(x) .. ", " .. math.floor(y) .. " is not loaded on the server (it only keeps the surroundings of players)") end
	local spawned, unknown, n = 0, array(), 0
	for _, w in ipairs(wanted) do
		for i = 1, w.count do
			-- a 4 x 4 grid of places on the square, round and round
			local ox, oy = 0.2 + (n % 4) * 0.2, 0.2 + (math.floor(n / 4) % 4) * 0.2
			-- an unknown type gives no item; an error must not lose the count of what was already put down
			local item = try(function() return sq:AddWorldInventoryItem(w.fullType, ox, oy, 0) end)
			if not item then
				unknown[#unknown + 1] = w.fullType
				break
			end
			spawned = spawned + 1
			n = n + 1
		end
	end
	audit("put " .. spawned .. " items on the ground at " .. math.floor(x) .. ", " .. math.floor(y) .. ", " .. math.floor(z)
		.. (#unknown > 0 and (" (unknown: " .. table.concat(unknown, ", ") .. ")") or ""))
	return { spawned = spawned, unknown = unknown }
end

-- ---- deaths (bridge v8) ----
-- IsoGameCharacter.DoDeath runs on the server for a player who dies (it writes "<name> died at" to the
-- user log there) and begins with OnDeath, which fires OnCharacterDeath: kept here, the last
-- MAX_DEATHS of them, and in a file of the Lua folder so that a restart does not lose them.

local DEATHS_FILE = "spiffocon_deaths.txt"
local MAX_DEATHS = 100
local deaths = nil

-- first line of the file since bridge v10. In B42 an animal is an IsoPlayer too (IsoAnimal), named "Bob"
-- as every IsoPlayer is at first: bridges v8 and v9 kept every dead animal as a death of "Bob". A file
-- without this line comes from them, and its "Bob" lines are dropped once.
local DEATHS_HEADER = "# SpiffoCON bridge deaths v10"

-- written once the "Bob" lines are gone: if the server goes back to bridge v8/v9 for a while (they rewrite
-- the file without the header), the next v10 does not drop the deaths of a real player named Bob again
local DEATHS_CLEANED_FILE = "spiffocon_deaths_v10.txt"

local function deathsCleaned()
	local reader = getFileReader(DEATHS_CLEANED_FILE, false)
	if not reader then return false end
	reader:close()
	return true
end

local function markDeathsCleaned()
	local writer = getFileWriter(DEATHS_CLEANED_FILE, true, false)
	writer:write("The deaths of animals kept by bridges v8 and v9 were dropped from spiffocon_deaths.txt.\n")
	writer:close()
end

local function isAnimal(character)
	return try(function() return character:isAnimal() end) or instanceof(character, "IsoAnimal")
end

local saveDeaths

-- one death per line: time<TAB>username<TAB>x<TAB>y<TAB>z<TAB>killer<TAB>hoursSurvived<TAB>zombieKills<TAB>characterName
-- (the character's name since bridge v10)
local function loadDeaths()
	deaths = {}
	local reader = getFileReader(DEATHS_FILE, false)
	if not reader then
		try(markDeathsCleaned)
		return
	end
	local line = reader:readLine()
	local old = line ~= nil and line ~= DEATHS_HEADER
	local clean = old and not deathsCleaned()
	local dropped = 0
	while line do
		local p = {}
		for part in (line .. "\t"):gmatch("([^\t]*)\t") do p[#p + 1] = part end
		if #p >= 5 and line:sub(1, 1) ~= "#" then
			if clean and p[2] == "Bob" then
				dropped = dropped + 1
			else
				local killer = p[6] ~= "" and p[6] or nil
				deaths[#deaths + 1] = { time = p[1], username = p[2], x = tonumber(p[3]), y = tonumber(p[4]), z = tonumber(p[5]),
					killer = killer, hoursSurvived = tonumber(p[7]), zombieKills = tonumber(p[8]),
					characterName = p[9] ~= "" and p[9] or nil }
			end
		end
		line = reader:readLine()
	end
	reader:close()
	if old then
		-- marked only once the cleaned file is saved, or the next start would keep the "Bob" lines for good
		if pcall(saveDeaths) and clean then
			try(markDeathsCleaned)
			if dropped > 0 then print("SpiffoCON bridge: dropped " .. dropped .. " deaths of animals kept by an older bridge") end
		end
	elseif not deathsCleaned() then
		-- a v10 file from before the marker existed
		try(markDeathsCleaned)
	end
end

saveDeaths = function()
	local lines = { DEATHS_HEADER }
	for _, d in ipairs(deaths) do
		lines[#lines + 1] = table.concat({ d.time, d.username, tostring(d.x), tostring(d.y), tostring(d.z), d.killer or "",
			tostring(d.hoursSurvived or ""), tostring(d.zombieKills or ""), ((d.characterName or ""):gsub("[\t\r\n]", " ")) }, "\t")
	end
	local writer = getFileWriter(DEATHS_FILE, true, false)
	writer:write(table.concat(lines, "\n") .. "\n")
	writer:close()
end

local function onCharacterDeath(character)
	if not instanceof(character, "IsoPlayer") or isAnimal(character) then return end
	if not deaths then loadDeaths() end
	-- the killer is the game's last attacker (IsoGameCharacter.setAttackedBy: hits, bites, fire, vehicles,
	-- Kill). An animal's attack does not set it (AnimalAttackState → hitConsequences → DamageFromAnimal), so
	-- an animal is named only when the game happens to; otherwise the last attacker before it shows
	local killer = try(function()
		local by = character:getAttackedBy()
		if by and isAnimal(by) then
			local kind = try(function() return by:getAnimalType() end)
			return kind and ("animal:" .. kind) or "animal"
		end
		if by and by ~= character and instanceof(by, "IsoPlayer") then return by:getUsername() end
		if by and instanceof(by, "IsoZombie") then return "zombie" end
		return nil
	end)
	deaths[#deaths + 1] = {
		-- milliseconds since 1970, as a string (a Java long through Lua loses digits as a number)
		time = string.format("%.0f", getTimestampMs()),
		username = tostring(try(function() return character:getUsername() end)),
		x = try(function() return math.floor(character:getX()) end),
		y = try(function() return math.floor(character:getY()) end),
		z = try(function() return math.floor(character:getZ()) end),
		killer = killer,
		hoursSurvived = try(function() return math.floor(character:getHoursSurvived() * 10) / 10 end),
		zombieKills = try(function() return character:getZombieKills() end),
		characterName = characterName(character),
	}
	while #deaths > MAX_DEATHS do table.remove(deaths, 1) end
	try(saveDeaths)
end

local function listDeaths()
	if not deaths then loadDeaths() end
	local result = array()
	for _, d in ipairs(deaths) do result[#result + 1] = d end
	return result
end

-- ---- zombies on the map (bridge v8) ----

-- zombiecells [size]: the zombies of the loaded areas, counted per square of size x size (10 by default),
-- for a density layer on the map: { x, y, n } with x, y the corner of the square
local function zombieCells(size)
	size = math.floor(tonumber(size) or 10)
	if not (size >= 2 and size <= 100) then size = 10 end
	local counts, order = {}, array()
	local zombies = getCell():getZombieList()
	for i = 0, zombies:size() - 1 do
		local zombie = zombies:get(i)
		local cx, cy = math.floor(zombie:getX() / size) * size, math.floor(zombie:getY() / size) * size
		local key = cx .. "," .. cy
		local cell = counts[key]
		if not cell then
			cell = { x = cx, y = cy, n = 0 }
			counts[key] = cell
			order[#order + 1] = cell
		end
		cell.n = cell.n + 1
	end
	return order
end

-- ---- wrecks (bridge v8) ----

-- the game's burnt and smashed vehicles (Base.CarNormalBurnt, Base.PickUpTruckSmashedFront...): shells
-- that can't be driven or repaired into a car again
local function isWreck(v)
	local script = try(function() return v:getScript():getFullName() end) or ""
	return script:find("Burnt", 1, true) ~= nil or script:find("Smashed", 1, true) ~= nil
end

-- removewrecks <x> <y> <radius> <apply: 0 | 1> [username]: the wrecks within the radius, counted, or with
-- apply removed as Commands.remove does (permanentlyRemove). One with someone inside stays.
local function removeWrecks(x, y, radius, apply, username)
	x, y, radius = areaOf("removewrecks", x, y, radius, username)
	apply = apply == "1"
	local found, removed = 0, 0
	local list = loadedVehicles()
	for i = 0, list:size() - 1 do
		local v = list:get(i)
		local vx, vy = try(function() return v:getX() end), try(function() return v:getY() end)
		if vx and vy and (vx - x) * (vx - x) + (vy - y) * (vy - y) <= radius * radius and isWreck(v)
			and not try(function() return v:getDriver() end) then
			found = found + 1
			if apply then
				v:permanentlyRemove()
				removed = removed + 1
			end
		end
	end
	if apply then audit("removed " .. removed .. " wrecks within " .. radius .. " squares of " .. x .. ", " .. y) end
	return { found = found, removed = removed, radius = radius }
end

-- ---- hair style (bridge v9) ----

local function hairStylesOf(p)
	local styles = getHairStylesInstance()
	return p:isFemale() and styles:getAllFemaleStyles() or styles:getAllMaleStyles()
end

-- hairstyles <username>: the hair styles a player of that gender can have (not the variants drawn under
-- hats), with the game's names, and the one they have now
local function hairStyles(username)
	local p = requirePlayer(username)
	local list = hairStylesOf(p)
	local result, seen = array(), {}
	for i = 0, list:size() - 1 do
		local s = list:get(i)
		local name = s:getName()
		if name and name ~= "" and not seen[name] and not s:isNoChoose() then
			seen[name] = true
			result[#result + 1] = {
				name = name,
				label = try(function() return getTextOrNull("IGUI_Hair_" .. name) end),
				level = try(function() return s:getLevel() end),
			}
		end
	end
	local current = try(function() return p:getHumanVisual():getHairModel() end)
	local reply = { female = p:isFemale(), current = current, styles = result }

	-- bridge v10: the beards (men only), the colours the game offers when a character is made, and theirs now
	local function rgb(c)
		return c and { r = try(function() return c:getRedFloat() end), g = try(function() return c:getGreenFloat() end),
			b = try(function() return c:getBlueFloat() end) } or nil
	end
	local visual = p:getHumanVisual()
	reply.hairColor = try(function() return rgb(visual:getHairColor()) end)
	if not p:isFemale() then
		reply.beard = try(function() return visual:getBeardModel() end)
		reply.beardColor = try(function() return rgb(visual:getBeardColor()) end)
		local beards = array()
		try(function()
			local all = getBeardStylesInstance():getAllStyles()
			for i = 0, all:size() - 1 do
				local s = all:get(i)
				local name = s:getName()
				if name and name ~= "" then
					beards[#beards + 1] = { name = name, label = try(function() return getTextOrNull("IGUI_Beard_" .. name) end),
						level = try(function() return s:getLevel() end) }
				end
			end
		end)
		reply.beards = beards
	end
	local colors = array()
	try(function()
		local common = p:getDescriptor():getCommonHairColor()
		for i = 0, common:size() - 1 do colors[#colors + 1] = rgb(common:get(i)) end
	end)
	reply.colors = colors
	return reply
end

-- sethair <username> <style>: gives the player that hair style, on the server's copy of the player (the
-- one that is saved) and to every client near them, as the game's own hair cut does (ISCutHair:complete
-- then sendHumanVisual, which on a server is GameServer.syncHumanVisual)
local function setHair(username, style)
	local p = requireLivingPlayer(username)
	local found = nil
	local list = hairStylesOf(p)
	for i = 0, list:size() - 1 do
		local s = list:get(i)
		if s:getName() == style and not s:isNoChoose() then found = s end
	end
	if not found then
		error("\"" .. tostring(style) .. "\" is not a hair style for " .. (p:isFemale() and "women" or "men"))
	end
	local visual = p:getHumanVisual()
	local before = visual:getHairModel()
	-- as ISCutHair:complete: a tied style (ponytail, bun) remembers the loose hair "Untie" gives back, any
	-- other style forgets it
	if try(function() return found:isAttachedHair() end) then
		-- only loose hair at least as long as the tied style: a tied one would make "Untie" tie again forever,
		-- a shorter one would turn a long ponytail into a short cut. Otherwise nil, and the game's character
		-- screen fills it in itself (the grow reference of that length) when the player unties
		if not visual:getNonAttachedHair() then
			local loose = nil
			for i = 0, list:size() - 1 do
				local s = list:get(i)
				if s:getName() == before then loose = s end
			end
			if loose and not try(function() return loose:isAttachedHair() end)
				and (try(function() return loose:getLevel() end) or 0) >= (try(function() return found:getLevel() end) or 0) then
				visual:setNonAttachedHair(before)
			end
		end
	else
		visual:setNonAttachedHair(nil)
	end
	-- as the game's hair cut: shaved, the hair is its natural colour again (a dye is gone)
	if string.lower(style) == "bald" then
		try(function() visual:setHairColor(visual:getNaturalHairColor()) end)
	end
	visual:setHairModel(style)
	p:resetModelNextFrame()
	sendHumanVisual(p)
	audit("set the hair style of " .. username .. " to " .. style .. " (was " .. tostring(before) .. ")")
	return { hair = visual:getHairModel(), before = before }
end

-- setbeard <username> <style>: a man's beard, "" for none (bridge v10), as setHair
local function setBeard(username, style)
	local p = requireLivingPlayer(username)
	style = style or ""
	-- a woman can lose one she got somehow (debug), not get one
	if p:isFemale() and style ~= "" then error(username .. " can't have a beard") end
	if style ~= "" then
		local found = false
		local all = getBeardStylesInstance():getAllStyles()
		for i = 0, all:size() - 1 do
			if all:get(i):getName() == style then found = true end
		end
		if not found then error("\"" .. style .. "\" is not a beard style") end
	end
	local visual = p:getHumanVisual()
	local before = visual:getBeardModel()
	-- as the game's beard trim (ISTrimBeard): shaved off, the beard is its natural colour again
	if style == "" then
		try(function() visual:setBeardColor(visual:getNaturalBeardColor()) end)
	end
	visual:setBeardModel(style)
	p:resetModelNextFrame()
	sendHumanVisual(p)
	audit("set the beard of " .. username .. " to " .. (style == "" and "none" or style) .. " (was " .. tostring(before) .. ")")
	return { beard = visual:getBeardModel(), before = before }
end

-- sethaircolor <username> <r> <g> <b>: the colour (0 to 1 each) of hair and beard, also as their natural colour,
-- as the game does when a character is made (bridge v10)
local function setHairColor(username, r, g, b)
	local p = requireLivingPlayer(username)
	local function channel(v)
		v = tonumber(v)
		if not v or not (v >= 0 and v <= 1) then error("a colour is three numbers from 0 to 1") end
		return v
	end
	r, g, b = channel(r), channel(g), channel(b)
	local color = ImmutableColor.new(r, g, b, 1)
	local visual = p:getHumanVisual()
	visual:setHairColor(color)
	visual:setNaturalHairColor(color)
	visual:setBeardColor(color)
	visual:setNaturalBeardColor(color)
	p:resetModelNextFrame()
	sendHumanVisual(p)
	audit(string.format("set the hair colour of %s to %.2f, %.2f, %.2f", username, r, g, b))
	return { r = r, g = g, b = b }
end

-- ---- zombie infection (bridge v10) ----

-- cureinfection <username>: the zombie infection gone. The server simulates the body in multiplayer
-- (BodyDamage.Update returns at once on a client). The infection is a flag of the whole body that
-- BodyDamage.Update sets from any infected part and never clears by itself, nor does a heal (the one above,
-- as the game's own). So: the parts first (or the next update infects the body again), then the body
-- (infection time and mortality back to -1, as a new character: a later bite starts its own countdown), the
-- infection and fever stats, each sent to the player.
local function cureInfection(username)
	local p = requireLivingPlayer(username)
	local body = p:getBodyDamage()
	local was = body:IsInfected()
	local parts = body:getBodyParts()
	for i = 0, parts:size() - 1 do
		local part = parts:get(i)
		if part:IsInfected() or part:IsFakeInfected() then
			-- a bite of this very tick may not have reached the body flag yet
			if part:IsInfected() then was = true end
			part:SetInfected(false)
			part:SetFakeInfected(false)
			syncBodyPart(part, 0xFFFFFFFFFFF)
		end
	end
	body:setInfected(false)
	body:setIsFakeInfected(false)
	body:setReduceFakeInfection(false)
	body:setInfectionTime(-1)
	body:setInfectionMortalityDuration(-1)
	local stats = p:getStats()
	stats:reset(CharacterStat.ZOMBIE_INFECTION)
	stats:reset(CharacterStat.ZOMBIE_FEVER)
	local mask = (try(function() return SyncPlayerStatsPacket.getBitMaskForStat(CharacterStat.ZOMBIE_INFECTION) end) or 0)
		+ (try(function() return SyncPlayerStatsPacket.getBitMaskForStat(CharacterStat.ZOMBIE_FEVER) end) or 0)
	if mask > 0 then syncPlayerStats(p, mask) end
	-- the whole body to the player's game too (time and mortality of the infection are not in the parts):
	-- the game's own actions send it so (ISDryMyself, ISDrinkFromBottle)
	sendDamage(p)
	audit("cured the zombie infection of " .. username .. (was and "" or " (was not infected)"))
	return { wasInfected = was, infected = body:IsInfected() }
end

-- after a Lua reload the previous copy's handler must go, or each death would be written twice
if SpiffoCONBridgeDeath then Events.OnCharacterDeath.Remove(SpiffoCONBridgeDeath) end
SpiffoCONBridgeDeath = onCharacterDeath
Events.OnCharacterDeath.Add(onCharacterDeath)

local actions = {
	hairstyles = hairStyles,
	setbeard = setBeard,
	sethaircolor = setHairColor,
	cureinfection = cureInfection,
	sethair = setHair,
	spawnitems = spawnItems,
	deaths = listDeaths,
	zombiecells = zombieCells,
	removewrecks = removeWrecks,
	fixfluids = fixFluids,
	itemaction = itemAction,
	removecorpses = removeCorpses,
	removegrounditems = removeGroundItems,
	stopfires = stopFires,
	safehouses = listSafehouses,
	removesafehouse = removeSafehouse,
	playerdetails = playerDetails,
	settime = setTime,
	vehiclekey = vehicleKey,
	ping = function() return { version = VERSION, players = getOnlinePlayers():size() } end,
	players = function() return listPlayers() end,
	inventory = function(username) return inventory(username) end,
	vehicles = function() return vehicles() end,
	world = function() return world() end,
	heal = heal,
	removeitem = removeItem,
	repairvehicle = repairVehicle,
	repairpart = repairPart,
	vehicle = vehicleDetails,
	repairvehicleof = repairVehicleOf,
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
			local ok, result = pcall(handler, request[3], request[4], request[5], request[6], request[7], request[8])
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
	-- every tick, right after GameTime.update (OnTick comes after it): the night a time skip left to
	-- count, as soon as the game has turned the day
	if nightToCount then
		pcall(function()
			local t = getGameTime()
			if t:getTimeOfDay() < 24 then
				nightToCount = false
				t:setNightsSurvived(t:getNightsSurvived() + 1)
			end
		end)
	end
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
