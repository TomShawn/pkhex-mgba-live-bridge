-- Exercise the actual server script with deterministic emulator and socket APIs.
memory = {}
frame = 100
fail_write_at = nil
function put32(address, value)
    for index = 0, 3 do memory[address + index] = (value >> (index * 8)) & 255 end
end
function get32(address)
    local value = 0
    for index = 0, 3 do value = value | ((memory[address + index] or 0) << (index * 8)) end
    return value
end
emu = {
    read8 = function(_, address) return memory[address] or 0 end,
    read32 = function(_, address) return get32(address) end,
    readRange = function(_, address, length)
        local bytes = {}
        for index = 0, length - 1 do bytes[#bytes + 1] = string.char(memory[address + index] or 0) end
        return table.concat(bytes)
    end,
    write8 = function(_, address, value)
        if fail_write_at == address then fail_write_at = nil return end
        memory[address] = value
    end,
    currentFrame = function() return frame end,
    checksum = function() return string.char(0xad, 0x89, 0xa8, 0x94) end,
}
put32(0x03005D8C, 0x02025000)
put32(0x03005D90, 0x02035000)
put32(0x03005D94, 0x02029000)
memory[0x020244E9] = 4
console = { log = function() end }
C = { CHECKSUM = { CRC32 = 1 } }
callbacks = { entries = {}, next_id = 0 }
function callbacks:add(event, handler)
    self.next_id = self.next_id + 1
    self.entries[self.next_id] = { event = event, handler = handler }
    return self.next_id
end
function callbacks:remove(identity) self.entries[identity] = nil end
function fire(event)
    for _, entry in pairs(callbacks.entries) do
        if entry.event == event then entry.handler() end
    end
end
socket = { ERRORS = { AGAIN = 4 } }
server = { handlers = {} }
function server:listen() return true end
function server:add(event, handler) self.handlers[event] = handler end
function server:accept() local peer = self.pending self.pending = nil return peer end
function server:close() self.closed = true end
socket.bind = function() return server end
GBA_BRIDGE_CONFIG = { port = 12345, token = 'token', crc = 'ad89a894', session = 'fixture' }

function connect()
    local peer = { handlers = {}, output = '', input = '' }
    function peer:add(event, handler) self.handlers[event] = handler end
    function peer:send(data)
        if not self.drop_replies then self.output = self.output .. data end
        return #data
    end
    function peer:receive()
        if self.remote_eof then return nil, "disconnected" end
        if #self.input == 0 then return nil, socket.ERRORS.AGAIN end
        local data = self.input
        self.input = ''
        return data
    end
    function peer:close() self.closed = true end
    server.pending = peer
    server.handlers.received()
    request(peer, 'AUTH token')
    return peer
end
function request(peer, command)
    peer.output = ''
    peer.input = command .. '\n'
    peer.handlers.received()
    return peer.output
end
function transaction(peer, identity, session, epoch, address, before, after)
    return request(peer, string.format('BEGIN %s %s %d 2025000 2035000 2029000\nPATCH %x %s %s\nCOMMIT %s',
        identity, session, epoch, address, before, after, identity))
end
