-- Dumps server and sandbox option metadata for SpiffoCON's tools/MakeServerOptions.cs.
-- Copy into <dedicated server>/media/lua/server/, start the server once with
-- -Duser.language=en, then remove it. Output goes to <cachedir>/Lua/:
--   spiffocon_options.json  server options (the .ini)
--   spiffocon_sandbox.json  sandbox options (SandboxVars.lua), with the values in effect
local function esc(s)
	s = tostring(s)
	s = s:gsub('\\', '\\\\'):gsub('"', '\\"'):gsub('\n', '\\n'):gsub('\r', ''):gsub('\t', ' ')
	return s
end

local function field(name, value)
	return '"' .. name .. '":"' .. esc(value) .. '"'
end

local function try(f)
	local ok, v = pcall(f)
	if ok then return v end
	return nil
end

-- source: object with getNumOptions/getOptionByIndex; sandbox: also name, page, current value
local function dump(fileName, source, sandbox)
	local w = getFileWriter(fileName, true, false)
	w:write('[\n')
	local n = source:getNumOptions()
	for i = 1, n do
		local option = source:getOptionByIndex(i - 1)
		local o = option:asConfigOption()
		local parts = { field("name", o:getName()), field("type", o:getType()), field("default", o:getDefaultValue()) }
		local tip = try(function() return option:getTooltip() end)
		if tip then table.insert(parts, field("tooltip", tip)) end
		local min = try(function() return o:getMin() end)
		if min ~= nil then table.insert(parts, field("min", min)) end
		local max = try(function() return o:getMax() end)
		if max ~= nil then table.insert(parts, field("max", max)) end
		if o:getType() == "enum" then
			local values = {}
			local count = try(function() return o:getNumValues() end) or 0
			for k = 1, count do
				local text = try(function() return option:getValueTranslationByIndex(k) end)
					or try(function() return o:getValueTranslationByIndex(k) end) or tostring(k)
				table.insert(values, '"' .. esc(text) .. '"')
			end
			table.insert(parts, '"values":[' .. table.concat(values, ',') .. ']')
		end
		if sandbox then
			local title = try(function() return option:getTranslatedName() end)
			if title then table.insert(parts, field("title", title)) end
			local page = try(function() return option:getPageName() end)
			if page then table.insert(parts, field("page", page)) end
			table.insert(parts, field("value", o:getValueAsString()))
		end
		w:write('{' .. table.concat(parts, ',') .. '}' .. (i < n and ',' or '') .. '\n')
	end
	w:write(']\n')
	w:close()
	print("SPIFFOCON DUMP DONE " .. fileName .. " " .. tostring(n))
end

local ok, err = pcall(dump, "spiffocon_options.json", ServerOptions.new(), false)
if not ok then print("SPIFFOCON DUMP FAILED options " .. tostring(err)) end

-- sandbox values are only final once the world is loaded
local function dumpSandbox()
	local ok2, err2 = pcall(dump, "spiffocon_sandbox.json", getSandboxOptions(), true)
	if not ok2 then print("SPIFFOCON DUMP FAILED sandbox " .. tostring(err2)) end
end
if Events.OnServerStarted then
	Events.OnServerStarted.Add(dumpSandbox)
else
	Events.OnGameBoot.Add(dumpSandbox)
end
