-- Temporary: dumps server option metadata for SpiffoCON (removed after use).
local function esc(s)
	s = tostring(s)
	s = s:gsub('\\', '\\\\'):gsub('"', '\\"'):gsub('\n', '\\n'):gsub('\r', ''):gsub('\t', ' ')
	return s
end

local function field(name, value)
	return '"' .. name .. '":"' .. esc(value) .. '"'
end

local function dumpOptions()
	local w = getFileWriter("spiffocon_options.json", true, false)
	w:write('[\n')
	local options = ServerOptions.new()
	local n = options:getNumOptions()
	for i = 1, n do
		local o = options:getOptionByIndex(i - 1):asConfigOption()
		local parts = { field("name", o:getName()), field("type", o:getType()) }
		table.insert(parts, field("default", o:getDefaultValue()))
		local ok, tip = pcall(function() return o:getTooltip() end)
		if ok and tip then table.insert(parts, field("tooltip", tip)) end
		local okMin, min = pcall(function() return o:getMin() end)
		if okMin and min ~= nil then table.insert(parts, field("min", min)) end
		local okMax, max = pcall(function() return o:getMax() end)
		if okMax and max ~= nil then table.insert(parts, field("max", max)) end
		if o:getType() == "enum" then
			local values = {}
			for k = 1, o:getNumValues() do
				table.insert(values, '"' .. esc(o:getValueTranslationByIndex(k)) .. '"')
			end
			table.insert(parts, '"values":[' .. table.concat(values, ',') .. ']')
		end
		w:write('{' .. table.concat(parts, ',') .. '}' .. (i < n and ',' or '') .. '\n')
	end
	w:write(']\n')
	w:close()
	print("SPIFFOCON DUMP DONE " .. tostring(n))
end

local ok, err = pcall(dumpOptions)
if not ok then print("SPIFFOCON DUMP FAILED " .. tostring(err)) end
