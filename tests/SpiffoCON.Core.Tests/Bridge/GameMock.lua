-- A mock of the game for the whole SpiffoCON bridge script: just enough of the API it calls.
FILES = {}
NOW = 1000
LOG = {}          -- what the game was told to do (sync calls, admin log)
local function note(text) LOG[#LOG + 1] = text end

function isServer() return true end
function getTimestampMs() return NOW end
Events = { OnTick = { Add = function() end, Remove = function() end },
	OnCharacterDeath = { Add = function(f) DEATH_HANDLER = f end, Remove = function() DEATH_HANDLER = nil end } }

function getFileReader(name)
	local text = FILES[name]
	if not text then return nil end
	local lines = {}
	for line in (text .. "\n"):gmatch("([^\n]*)\n") do lines[#lines + 1] = line end
	if lines[#lines] == "" then lines[#lines] = nil end
	local i = 0
	return { readLine = function() i = i + 1; return lines[i] end, close = function() end }
end
function getFileWriter(name)
	local parts = {}
	return { write = function(self, text) parts[#parts + 1] = text end, close = function() FILES[name] = table.concat(parts) end }
end

local function list(items)
	return {
		items = items,
		size = function(self) return #items end,
		get = function(self, i) return items[i + 1] end,
		contains = function(self, o) for _, v in ipairs(items) do if v == o then return true end end return false end,
		addAll = function(self, other) for _, v in ipairs(other.items) do items[#items + 1] = v end end,
	}
end
ArrayList = { new = function() return list({}) end }
function instanceof(o, class) return type(o) == "table" and o.class == class end
function writeLog(category, text) note("log " .. category .. ": " .. text) end
function sendAddItemToContainer(container, item) note("sendAdd " .. item.name) end
function sendRemoveItemFromContainer(container, item) note("sendRemove " .. item.name .. " from " .. container.name) end
function getSteamIDFromUsername(name) return "76561198000000001" end

-- ---- items and containers ----
local function container(name)
	local items = {}
	local c = { name = name }
	c.getItems = function() return list(items) end
	c.Remove = function(self, item) for i, v in ipairs(items) do if v == item then table.remove(items, i) return end end end
	c.AddItem = function(self, item) items[#items + 1] = item; item.container = c end
	c.count = function() return #items end
	return c
end
local function item(fullType, name)
	local it = { name = name, fullType = fullType }
	it.getFullType = function() return fullType end
	it.getDisplayName = function() return name end
	it.getContainer = function() return it.container end
	it.isEquipped = function() return false end
	return it
end
local function bag(fullType, name)
	local b = item(fullType, name)
	b.class = "InventoryContainer"
	b.inner = container(name)
	b.getInventory = function() return b.inner end
	return b
end

-- ---- players ----
Perks = { None = { name = "None" } }
local function perk(name, parent) return { getName = function() return name end, getParent = function() return parent end } end
local combat = { getName = function() return "Combat" end, getParent = function() return Perks.None end }
local crafting = { getName = function() return "Crafting" end, getParent = function() return Perks.None end }
local axe, carpentry = perk("Axe", combat), perk("Carpentry", crafting)
PerkFactory = { PerkList = list({ combat, crafting, axe, carpentry }) }
local function stat(min, max) return { getMinimumValue = function() return min end, getMaximumValue = function() return max end } end
CharacterStat = { HUNGER = stat(0, 1), THIRST = stat(0, 1), FATIGUE = stat(0, 1), ENDURANCE = stat(0, 1), STRESS = stat(0, 1),
	PANIC = stat(0, 100), BOREDOM = stat(0, 100), UNHAPPINESS = stat(0, 100), PAIN = stat(0, 100), INTOXICATION = stat(0, 100), SICKNESS = stat(0, 1) }
CharacterTraitDefinition = { getCharacterTraitDefinition = function(trait)
	if trait == "base:mystery" then return nil end
	return { getLabel = function() return trait == "base:brave" and "Brave" or "Fast Reader" end }
end }

local function player(username, x, y, z)
	local p = { class = "IsoPlayer", username = username, x = x, y = y, z = z, inventory = container("inventory of " .. username) }
	p.getAttackedBy = function() return p.attackedBy end
	p.getUsername = function() return username end
	p.getDisplayName = function() return username end
	p.getX = function() return p.x end
	p.getY = function() return p.y end
	p.getZ = function() return p.z end
	p.isDead = function() return false end
	p.isGodMod = function() return false end
	p.isInvisible = function() return false end
	p.isNoClip = function() return false end
	p.getHoursSurvived = function() return 12.34 end
	p.getZombieKills = function() return 7 end
	p.getInventory = function() return p.inventory end
	p.isEquipped = function() return false end
	p.getDescriptor = function() return { getCharacterProfession = function() return { getName = function() return "Carpenter" end } end } end
	p.getRole = function() return { getName = function() return "user" end } end
	p.getVehicle = function() return nil end
	p.getBodyDamage = function() return {
		getOverallBodyHealth = function() return 87.6 end,
		IsInfected = function() return username == "kate" end,
		getNumPartsBitten = function() return username == "kate" and 1 or 0 end,
		IsOnFire = function() return false end,
	} end
	p.getCharacterTraits = function() return { getKnownTraits = function() return list({ "base:brave", "base:fastreader", "base:mystery" }) end } end
	p.getPerkLevel = function(self, k) return k == axe and 4 or 2 end
	p.getStats = function() return { get = function(self, s)
		if s == CharacterStat.HUNGER then return 0.256 end
		if s == CharacterStat.PANIC then return 50 end
		if s == CharacterStat.ENDURANCE then return 1 end
		return 0
	end } end
	return p
end

rj = player("rj", 100.6, 102.9, 0)
kate = player("kate", 500.2, 500.7, 1)
PLAYERS = { rj, kate }
function getOnlinePlayers() return list(PLAYERS) end

-- rj: two school bags (nails in both), a toolbox in the first
bagA, bagB = bag("Base.Bag_Schoolbag", "School Bag"), bag("Base.Bag_Schoolbag", "School Bag")
local toolbox = bag("Base.Toolbox", "Toolbox")
rj.inventory:AddItem(item("Base.Axe", "Axe"))
rj.inventory:AddItem(bagA)
rj.inventory:AddItem(bagB)
for i = 1, 3 do bagA.inner:AddItem(item("Base.Nails", "Nails")) end
bagA.inner:AddItem(toolbox)
toolbox.inner:AddItem(item("Base.Nails", "Nails"))
for i = 1, 5 do bagB.inner:AddItem(item("Base.Nails", "Nails")) end
-- and two duffel bags of different types under one name (the game has 18 "Duffel Bag")
local duffelA, duffelB = bag("Base.Bag_DuffelBag", "Duffel Bag"), bag("Base.Bag_DuffelBagTINT", "Duffel Bag")
rj.inventory:AddItem(duffelA)
rj.inventory:AddItem(duffelB)
duffelA.inner:AddItem(item("Base.Screws", "Screws"))
for i = 1, 2 do duffelB.inner:AddItem(item("Base.Screws", "Screws")) end

-- ---- world ----
local world = {}
local function key(x, y, z) return x .. "," .. y .. "," .. z end
SAFE_SQUARES = {}
KNOWN_TYPES = { ["Base.Axe"] = true, ["Base.Nails"] = true, ["Base.WaterBottle"] = true }
SPAWNED_AT = {}
local function square(x, y, z)
	local sq = { x = x, y = y, z = z, corpses = {}, ground = {}, fires = {} }
	sq.getStaticMovingObjects = function() return list(sq.corpses) end
	sq.getWorldObjects = function() return list(sq.ground) end
	sq.getChunk = function() return { getMinLevel = function() return -1 end, getMaxLevel = function() return 2 end } end
	sq.removeCorpse = function(self, body, remote)
		for i, b in ipairs(sq.corpses) do if b == body then table.remove(sq.corpses, i); note("removeCorpse " .. body.name); return end end
		error("body not here")
	end
	sq.transmitRemoveItemFromSquare = function(self, object)
		for i, o in ipairs(sq.ground) do if o == object then table.remove(sq.ground, i); note("removeGround " .. object.name); return end end
		error("object not here")
	end
	-- as the game: any IsoFire counts, the permanent flame of a lit campfire too
	sq.haveFire = function() return #sq.fires > 0 end
	-- as the game with a type name: null for a type it does not know, else the item on the floor
	sq.AddWorldInventoryItem = function(self, fullType, ox, oy, oz)
		assert(type(fullType) == "string" and type(ox) == "number" and type(oy) == "number" and type(oz) == "number")
		if not KNOWN_TYPES[fullType] then return nil end
		local object = { name = fullType, getOffZ = function() return oz end, removeFromWorld = function() end, removeFromSquare = function() end, setSquare = function() end }
		sq.ground[#sq.ground + 1] = object
		SPAWNED_AT[#SPAWNED_AT + 1] = ox .. "/" .. oy
		note("spawn " .. fullType)
		return { getFullType = function() return fullType end }
	end
	sq.getObjects = function() return list(sq.fires) end
	world[key(x, y, z)] = sq
	return sq
end
-- IsoFireManager only knows the fires that are not permanent: those go, a campfire stays lit
function stopFire(sq)
	for i = #sq.fires, 1, -1 do if not sq.fires[i].permanent then table.remove(sq.fires, i) end end
	note("stopFire " .. sq.x .. "," .. sq.y .. "," .. sq.z)
end
local function fire(permanent) return { class = "IsoFire", permanent = permanent, isPermanent = function() return permanent end } end
for x = 90, 110 do for y = 90, 110 do for z = -1, 2 do square(x, y, z) end end end

local function corpse(name, kind)
	return { name = name, class = "IsoDeadBody",
		isZombie = function() return kind == "zombie" end, isPlayer = function() return kind == "player" end, isAnimal = function() return kind == "animal" end }
end
-- height: 0 on the floor, more on a table or a shelf
local function ground(name, height)
	return { name = name, getOffZ = function() return height or 0 end, removeFromWorld = function() end, removeFromSquare = function() end, setSquare = function() end }
end
local function at(x, y, z) return world[key(x, y, z)] end
table.insert(at(100, 100, 0).corpses, corpse("z1", "zombie"))
table.insert(at(100, 100, 0).corpses, corpse("dead player", "player"))
table.insert(at(101, 100, -1).corpses, corpse("z-basement", "zombie"))
table.insert(at(100, 104, 0).corpses, corpse("z-far", "zombie"))
table.insert(at(100, 100, 0).ground, ground("can"))
table.insert(at(100, 101, 2).ground, ground("plank upstairs"))
table.insert(at(102, 100, 0).ground, ground("gun in the safehouse"))
table.insert(at(102, 100, 0).ground, ground("ammo in the safehouse"))
table.insert(at(100, 106, 0).ground, ground("far away"))
table.insert(at(101, 100, 0).ground, ground("vase on a table", 0.42))
table.insert(at(100, 100, 0).fires, fire(false))
table.insert(at(101, 101, 1).fires, fire(false))
table.insert(at(100, 107, 0).fires, fire(false))
table.insert(at(99, 100, 0).fires, fire(true))    -- a lit campfire

local zombies = {}
local function zombie(x, y) zombies[#zombies + 1] = { class = "IsoZombie", getX = function() return x end, getY = function() return y end } end
for i = 1, 5 do zombie(100 + i, 100) end   -- near rj
zombie(140, 140)                           -- near nobody
zombie(510, 510)                           -- near kate
A_ZOMBIE = zombies[1]

-- ---- vehicles ----
VEHICLES = {}
local function vehicle(id, script, x, y, driver)
	local v = { id = id }
	v.getId = function() return id end
	v.getScript = function() return { getFullName = function() return script end } end
	v.getX = function() return x end
	v.getY = function() return y end
	v.getZ = function() return 0 end
	v.getDriver = function() return driver end
	v.isEngineRunning = function() return false end
	v.permanentlyRemove = function()
		for i, other in ipairs(VEHICLES) do if other == v then table.remove(VEHICLES, i) end end
		note("removeVehicle " .. script .. " #" .. id)
	end
	VEHICLES[#VEHICLES + 1] = v
	return v
end
local car = vehicle(7, "Base.PickUpTruck", 105, 100, nil)
car.createVehicleKey = function() return item("Base.CarKey", "Pick-up Truck Key") end
-- wrecks: burnt and smashed shells of the game
vehicle(8, "Base.CarNormalBurnt", 102, 101, nil)
vehicle(9, "Base.PickUpTruckSmashedFront", 98, 99, nil)
vehicle(10, "Base.OffRoadBurnt", 99, 101, { getUsername = function() return "kate" end })   -- someone inside
vehicle(11, "Base.ModernCarBurnt", 140, 140, nil)                                          -- far away
function getVehicleById(id) for _, v in ipairs(VEHICLES) do if v.id == id then return v end end return nil end

function getCell()
	return {
		getGridSquare = function(self, x, y, z) return world[key(x, y, z)] end,
		getZombieList = function() return list(zombies) end,
		getVehicles = function() return list(VEHICLES) end,
	}
end

-- ---- safehouses ----
local safehouses = {}
function safehouse(id, title, owner, members, x, y, w, h)
	local s = { id = id, hitPoints = 0 }
	s.getOnlineID = function() return id end
	s.getTitle = function() return title end
	s.getOwner = function() return owner end
	-- as the game: the owner is in the list too
	local all = { owner }
	for _, m in ipairs(members) do all[#all + 1] = m end
	s.getPlayers = function() return list(all) end
	s.getX = function() return x end
	s.getY = function() return y end
	s.getW = function() return w end
	s.getH = function() return h end
	s.getLastVisited = function() return 1790947308993 end
	s.getPlayerConnected = function() return 1 end
	s.setHitPoints = function(self, n) s.hitPoints = n end
	s.getHitPoints = function() return s.hitPoints end
	s.contains = function(sx, sy) return sx >= x and sx < x + w and sy >= y and sy < y + h end
	safehouses[#safehouses + 1] = s
	return s
end
safehouse(1, "rj's Safehouse", "rj", { "kate" }, 102, 98, 5, 5)
safehouse(2, "Kate \"the\" base", "kate", {}, 480, 480, 10, 12)
WAR_HIT_POINTS = 3
SafeHouse = {
	getSafehouseList = function() return list(safehouses) end,
	getSafeHouse = function(arg)
		for _, s in ipairs(safehouses) do
			if type(arg) == "number" then
				if s.id == arg then return s end
			elseif s.contains(arg.x, arg.y) then return s end
		end
		return nil
	end,
	removeSafeHouse = function(s) for i, v in ipairs(safehouses) do if v == s then table.remove(safehouses, i) end end note("removeSafeHouse (not synced) " .. s.id) end,
	hitPoint = function(id)
		for i, s in ipairs(safehouses) do
			if s.id == id then
				if s.hitPoints + 1 == WAR_HIT_POINTS then table.remove(safehouses, i); note("SafehouseRelease to all " .. id)
				else s.hitPoints = s.hitPoints + 1; note("SafehouseSync " .. id) end
				return
			end
		end
	end,
}
function getServerOptions() return { getInteger = function(self, name) if name == "WarSafehouseHitPoints" then return WAR_HIT_POINTS end error("no option") end } end

-- ---- time and weather ----
-- as zombie.GameTime: the age of the world is its nights (each begins at 7:00) plus the time of day
CLOCK = { time = 20.5, nights = 36, day = 14 }
function CLOCK.age() return CLOCK.nights * 24 + (CLOCK.time >= 7 and CLOCK.time - 7 or CLOCK.time + 17) end
-- one tick of GameTime.update on a server: a new night as the clock passes 7:00, the calendar at 24:00
function CLOCK.tick()
	local before = CLOCK.time
	CLOCK.time = CLOCK.time + 0.0005
	if before <= 7 and CLOCK.time > 7 then CLOCK.nights = CLOCK.nights + 1 end
	if CLOCK.time >= 24 then CLOCK.time = CLOCK.time - 24; CLOCK.day = CLOCK.day + 1 end
end
function getGameTime()
	return {
		getYear = function() return 1993 end, getMonth = function() return 7 end, getDay = function() return CLOCK.day end,
		getHour = function() return math.floor(CLOCK.time) end,
		getMinutes = function() return math.floor((CLOCK.time - math.floor(CLOCK.time)) * 60) end,
		getWorldAgeHours = function() return CLOCK.age() end,
		getTimeOfDay = function() return CLOCK.time end,
		setTimeOfDay = function(self, h) CLOCK.time = h; note("setTimeOfDay " .. h) end,
		getNightsSurvived = function() return CLOCK.nights end,
		setNightsSurvived = function(self, n) CLOCK.nights = n; note("setNightsSurvived " .. n) end,
	}
end
function getClimateManager() return {} end

-- ---- hair styles (bridge v9) ----
local function hairStyle(name, level, noChoose, attached)
	return { getName = function() return name end, getLevel = function() return level end, isNoChoose = function() return noChoose == true end,
		isAttachedHair = function() return attached == true end }
end
MALE_STYLES = { hairStyle("Bald", 0), hairStyle("Messy", 1), hairStyle("Donny", 2), hairStyle("HatPunkHat", 2, true), hairStyle("Messy", 1) }
FEMALE_STYLES = { hairStyle("Bald", 0), hairStyle("Hat", 1), hairStyle("Long2", 3), hairStyle("BunCurly", 2, false, true) }
function getHairStylesInstance()
	return { getAllMaleStyles = function() return list(MALE_STYLES) end, getAllFemaleStyles = function() return list(FEMALE_STYLES) end }
end
TRANSLATIONS = { IGUI_Hair_Messy = "Messy", IGUI_Hair_Long2 = "Long" }
function getTextOrNull(key) return TRANSLATIONS[key] end
function sendHumanVisual(p) note("sendHumanVisual " .. p:getUsername() .. " " .. tostring(p.visual.hair)) end
for _, p in ipairs(PLAYERS) do
	p.female = p.username == "kate"
	p.visual = { hair = p.female and "" or "Messy", nonAttached = "x" }
	p.isFemale = function() return p.female end
	p.getHumanVisual = function() return {
		getHairModel = function() return p.visual.hair end,
		setHairModel = function(self, h) p.visual.hair = h end,
		setNonAttachedHair = function(self, h) p.visual.nonAttached = h end,
		getNonAttachedHair = function() return p.visual.nonAttached end,
	} end
	p.resetModelNextFrame = function() note("resetModel " .. p.username) end
end

-- ---- animals (bridge v10): in B42 an animal is an IsoPlayer too, named "Bob" as every IsoPlayer at first ----
AN_ANIMAL = { class = "IsoPlayer", x = 300.5, y = 300.5, z = 0 }
AN_ANIMAL.isAnimal = function() return true end
AN_ANIMAL.getAnimalType = function() return "bull" end
AN_ANIMAL.getUsername = function() return "Bob" end
AN_ANIMAL.getX = function() return AN_ANIMAL.x end
AN_ANIMAL.getY = function() return AN_ANIMAL.y end
AN_ANIMAL.getZ = function() return AN_ANIMAL.z end
AN_ANIMAL.getAttackedBy = function() return nil end
for _, p in ipairs(PLAYERS) do p.isAnimal = function() return false end end

-- ---- bridge v10: character names, beards and colours, the zombie infection ----
local function color(r, g, b)
	return { r = r, g = g, b = b, getRedFloat = function() return r end, getGreenFloat = function() return g end, getBlueFloat = function() return b end }
end
ImmutableColor = { new = function(r, g, b, a) return color(r, g, b) end }
COMMON_HAIR_COLORS = { color(0.83, 0.67, 0.27), color(0.1, 0.08, 0.05), color(0.6, 0.3, 0.1) }
BEARD_STYLES = { hairStyle("Moustache", 1), hairStyle("Full", 2) }
function getBeardStylesInstance() return { getAllStyles = function() return list(BEARD_STYLES) end } end
CharacterStat.ZOMBIE_INFECTION = stat(0, 100)
CharacterStat.ZOMBIE_FEVER = stat(0, 100)
SyncPlayerStatsPacket = { getBitMaskForStat = function(s)
	if s == CharacterStat.ZOMBIE_INFECTION then return 4096 end
	if s == CharacterStat.ZOMBIE_FEVER then return 8192 end
	return 0
end }
function syncBodyPart(part, flags) note("syncBodyPart " .. part.name) end
function syncPlayerStats(p, mask) note("syncPlayerStats " .. p.username .. " " .. mask) end
function sendDamage(p) note("sendDamage " .. p.username) end

local names = { rj = { "Ray", "Jones" }, kate = { "Kate", "" } }
for _, p in ipairs(PLAYERS) do
	local name = names[p.username]
	local profession = { getName = function() return "Carpenter" end }
	p.getDescriptor = function() return {
		getCharacterProfession = function() return profession end,
		getForename = function() return name[1] end,
		getSurname = function() return name[2] end,
		getCommonHairColor = function() return list(COMMON_HAIR_COLORS) end,
	} end

	p.visual.hairColor = color(0.5, 0.5, 0.5)
	p.visual.beard = p.female and "" or "Moustache"
	p.visual.beardColor = color(0.5, 0.5, 0.5)
	local visual = p.getHumanVisual()
	visual.getHairColor = function() return p.visual.hairColor end
	visual.setHairColor = function(self, c) p.visual.hairColor = c end
	visual.setNaturalHairColor = function(self, c) p.visual.naturalHair = c end
	visual.getBeardModel = function() return p.visual.beard end
	visual.setBeardModel = function(self, b) p.visual.beard = b end
	visual.getBeardColor = function() return p.visual.beardColor end
	visual.setBeardColor = function(self, c) p.visual.beardColor = c end
	visual.setNaturalBeardColor = function(self, c) p.visual.naturalBeard = c end
	p.getHumanVisual = function() return visual end

	-- kate: bitten on the hand, infected, the infection under way
	local infected = p.username == "kate"
	local function part(name, isInfected)
		local bp = { name = name, infected = isInfected, fake = false }
		bp.IsInfected = function() return bp.infected end
		bp.IsFakeInfected = function() return bp.fake end
		bp.SetInfected = function(self, v) bp.infected = v end
		bp.SetFakeInfected = function(self, v) bp.fake = v end
		return bp
	end
	local parts = { part("Hand_L", infected), part("Torso_Upper", false) }
	local body = { infected = infected, time = infected and 30 or -1, mortality = infected and 48 or -1, fake = false }
	p.body = body
	p.parts = parts
	body.getOverallBodyHealth = function() return 87.6 end
	body.IsInfected = function() return body.infected end
	body.getNumPartsBitten = function() return infected and 1 or 0 end
	body.IsOnFire = function() return false end
	body.getBodyParts = function() return list(parts) end
	body.setInfected = function(self, v) body.infected = v end
	body.setIsFakeInfected = function(self, v) body.fake = v end
	body.setReduceFakeInfection = function(self, v) end
	body.setInfectionTime = function(self, v) body.time = v end
	body.setInfectionMortalityDuration = function(self, v) body.mortality = v end
	p.getBodyDamage = function() return body end

	local values = { [CharacterStat.HUNGER] = 0.256, [CharacterStat.PANIC] = 50, [CharacterStat.ENDURANCE] = 1,
		[CharacterStat.ZOMBIE_INFECTION] = infected and 40 or 0, [CharacterStat.ZOMBIE_FEVER] = infected and 20 or 0 }
	p.stats = values
	local stats = {
		get = function(self, s) return values[s] or 0 end,
		reset = function(self, s) values[s] = 0 end,
	}
	p.getStats = function() return stats end
end
