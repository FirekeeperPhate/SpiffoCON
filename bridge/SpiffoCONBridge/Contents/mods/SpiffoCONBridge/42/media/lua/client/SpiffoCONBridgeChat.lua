-- SpiffoCON Bridge, the players' side (bridge v17). It does one thing: a line an admin writes from
-- SpiffoCON is shown in the chat window, as a line of the General chat.
--
-- The server can send an alert by itself (servermsg), not an ordinary line of chat: so the bridge sends
-- the text to each game (server/SpiffoCONBridge.lua, the "chat" action) and this file shows it. Nothing is
-- sent back, nothing else is read or changed, and a failure here only means the line is not shown.
if isServer() then return end

-- the General chat is in the first tab of the chat window (ISChat.allChatStreams: tabID 1, counted from 1)
local GENERAL_TAB = 0
-- the colour of the General chat (zombie.chat.defaultChats.GeneralChat.getDefaultSettings: 255, 165, 0)
local COLOR = "<RGB:1.0,0.647,0.0>"

local function now()
	local ok, time = pcall(getHourMinute)
	return ok and time and tostring(time) or ""
end

-- What the chat window keeps of a line and asks of it (ISChat.addLineInChat, updateChatPrefixSettings):
-- the text with its prefix, composed as zombie.chat.ChatBase.getMessagePrefix does it (colour and size,
-- then time, chat and author as the player's chat settings want them), and the author for the mute list.
local function message(author, text)
	local m = { author = author, text = text, time = now() }
	function m:getAuthor() return self.author end
	function m:getText() return self.text end
	function m:setText(value) self.text = tostring(value) end
	function m:isServerAlert() return false end
	function m:getTextWithPrefix()
		local chat = ISChat.instance
		local prefix = COLOR .. " <SIZE:" .. tostring(chat and chat.chatFont or "medium") .. "> "
		if chat and chat.showTimestamp and self.time ~= "" then prefix = prefix .. "[" .. self.time .. "]" end
		if chat and chat.showTitle then prefix = prefix .. "[" .. getText("UI_chat_general_chat_title_id") .. "]" end
		-- as ChatMessage.getTextWithReplacedParentheses: what is typed is text, never a tag of the window
		local body = self.text:gsub("<", "&lt;"):gsub(">", "&gt;")
		return prefix .. "[" .. self.author .. "]: " .. body
	end
	-- a chat mod may ask a line something more: it is told nothing, instead of failing
	return setmetatable(m, { __index = function() return function() return nil end end })
end

local function show(author, text)
	local chat = ISChat and ISChat.instance
	if not chat or not chat.tabs then return end
	local tab = nil
	for _, candidate in ipairs(chat.tabs) do
		if candidate and candidate.tabID == GENERAL_TAB then tab = candidate end
	end
	tab = tab or chat.tabs[1]
	if not tab then return end
	ISChat.addLineInChat(message(author, text), tab.tabID)
end

local function onServerCommand(module, command, args)
	if module ~= "SpiffoCON" or command ~= "chat" or type(args) ~= "table" then return end
	local author, text = tostring(args.author or "Admin"), tostring(args.text or "")
	if text == "" then return end
	local ok, err = pcall(show, author, text)
	if not ok then print("SpiffoCON bridge: the chat line was not shown: " .. tostring(err)) end
end

-- after a Lua reload the previous copy's handler must go, or each line would be shown twice
if SpiffoCONBridgeChat then Events.OnServerCommand.Remove(SpiffoCONBridgeChat) end
SpiffoCONBridgeChat = onServerCommand
Events.OnServerCommand.Add(onServerCommand)
