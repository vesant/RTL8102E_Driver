using System;
using Cosmos.HAL.Network;
using Cosmos.System.Network.IPv4;

namespace RTL8102E_Driver
{
    public class CustomDHCP
    {
        private RTL8102E _driver;
        private bool _offerReceived = false;
        private bool _ackReceived = false;
        private byte[] _offeredIp = null;
        private byte[] _serverIp = null;
        private byte[] _subnetMask = new byte[] { 255, 255, 255, 0 };
        private byte[] _gatewayIp = null;
        private byte[] _dnsIp = null;

        public byte[] OfferedIp => _offeredIp;
        public byte[] SubnetMask => _subnetMask;
        public byte[] GatewayIp => _gatewayIp;
        public byte[] ServerIp => _serverIp;
        public byte[] DnsIp => _dnsIp;

        public CustomDHCP(RTL8102E driver)
        {
            _driver = driver;
        }

        public byte[] DoAutoConfig()
        {
            Console.WriteLine("[DHCP] Checking PHY Link Status before DHCP...");
            
            // Wait up to ~2.5s for PHY link auto-negotiation if cable was recently plugged in
            int linkSpins = 0;
            while (_driver != null && !_driver.IsPhyLinkUp() && linkSpins < 200000)
            {
                _driver.HackPoll();
                linkSpins++;
            }

            if (_driver != null && !_driver.IsPhyLinkUp())
            {
                Console.WriteLine("[DHCP] Notice: PHY Link is DOWN (Ethernet cable unplugged). Using Direct-Link fallback.");
                ApplyFallback();
                return _offeredIp;
            }

            Console.WriteLine("[DHCP] Starting Raw DHCP Discover (Broadcast)...");
            
            // Hijack DataReceived safely
            var oldHandler = _driver.DataReceived;
            _driver.DataReceived = CustomDataReceived;

            // Craft raw DHCP Discover Packet (Option 53 = 1)
            byte[] discoverPacket = BuildDhcpPacket(1, null, null);

            // Send initial Discover
            _driver.QueueBytes(discoverPacket);
            Console.WriteLine("[DHCP] Discover sent. Polling for Offer...");

            // Poll using HackPoll with bounded retries
            long timeout = 25000000L;
            long iter = 0;
            int retries = 0;
            
            while (!_offerReceived && iter < timeout)
            {
                _driver.HackPoll();
                iter++;
                
                // Re-transmit every 6 million iterations (~300ms)
                if (iter % 6000000 == 0)
                {
                    retries++;
                    Console.WriteLine($"[DHCP] Re-sending Discover (Retry {retries})...");
                    _driver.QueueBytes(discoverPacket);
                }
            }

            // Step 2: Send DHCP Request to commit lease with DHCP server (Option 53 = 3)
            if (_offerReceived && _offeredIp != null)
            {
                Console.WriteLine($"[DHCP] Offer received for {_offeredIp[0]}.{_offeredIp[1]}.{_offeredIp[2]}.{_offeredIp[3]}! Sending DHCP Request...");
                byte[] requestPacket = BuildDhcpPacket(3, _offeredIp, _serverIp);
                _driver.QueueBytes(requestPacket);

                long reqTimeout = 15000000L;
                long reqIter = 0;
                while (!_ackReceived && reqIter < reqTimeout)
                {
                    _driver.HackPoll();
                    reqIter++;
                    if (reqIter % 5000000 == 0)
                    {
                        _driver.QueueBytes(requestPacket);
                    }
                }

                if (_ackReceived)
                {
                    Console.WriteLine("[DHCP] ACK received! Lease committed.");
                }
            }

            // Restore normal handler
            _driver.DataReceived = oldHandler;

            if (_offerReceived && _offeredIp != null)
            {
                Console.WriteLine($"[DHCP] SUCCESS! IP: {_offeredIp[0]}.{_offeredIp[1]}.{_offeredIp[2]}.{_offeredIp[3]}");
                if (_gatewayIp != null)
                    Console.WriteLine($"[DHCP] Gateway: {_gatewayIp[0]}.{_gatewayIp[1]}.{_gatewayIp[2]}.{_gatewayIp[3]}");
                return _offeredIp;
            }
            
            Console.WriteLine("[DHCP] TIMEOUT! No DHCP Offer received. Falling back to Direct Link mode (192.168.1.180).");
            ApplyFallback();
            return _offeredIp;
        }

        private void ApplyFallback()
        {
            _offeredIp = new byte[] { 192, 168, 1, 180 };
            _gatewayIp = new byte[] { 192, 168, 1, 1 };
            _subnetMask = new byte[] { 255, 255, 255, 0 };
        }

        private void CustomDataReceived(byte[] packet)
        {
            if (packet == null || packet.Length < 42) return;
            
            ushort etherType = (ushort)((packet[12] << 8) | packet[13]);
            if (etherType != 0x0800) return; // Not IPv4
            
            byte protocol = packet[23];
            if (protocol != 17) return; // Not UDP

            ushort dstPort = (ushort)((packet[36] << 8) | packet[37]);
            if (dstPort != 68) return; // Not DHCP client port

            // Verify BootReply (Op = 2)
            if (packet[42] != 2) return;

            // Verify XID (0x12345678)
            if (packet[46] != 0x12 || packet[47] != 0x34 || packet[48] != 0x56 || packet[49] != 0x78) return;

            // Extract Your Client IP Address (YIAddr) at offset 58
            byte[] yiaddr = new byte[] { packet[58], packet[59], packet[60], packet[61] };
            
            // Check Magic Cookie at offset 278 (0x63, 0x82, 0x53, 0x63)
            int dhcpOff = 42;
            int udpLen = (packet[38] << 8) | packet[39];
            int dhcpEnd = 34 + udpLen;
            if (dhcpEnd > packet.Length) dhcpEnd = packet.Length;

            if (dhcpEnd >= dhcpOff + 240 && packet[278] == 0x63 && packet[279] == 0x82 && packet[280] == 0x53 && packet[281] == 0x63)
            {
                byte msgType = 0;
                int optIdx = 282; // Start of DHCP Options

                while (optIdx + 1 < dhcpEnd && packet[optIdx] != 255)
                {
                    byte opt = packet[optIdx];
                    if (opt == 0) { optIdx++; continue; }
                    byte len = packet[optIdx + 1];
                    if (len == 0 || optIdx + 2 + len > dhcpEnd) break;

                    if (opt == 53 && len >= 1) msgType = packet[optIdx + 2]; // 2 = Offer, 5 = ACK
                    if (opt == 1 && len >= 4) // Subnet Mask
                    {
                        _subnetMask = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }
                    if (opt == 3 && len >= 4) // Router (Gateway)
                    {
                        _gatewayIp = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }
                    if (opt == 6 && len >= 4) // DNS Server
                    {
                        _dnsIp = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }
                    if (opt == 54 && len >= 4) // DHCP Server Identifier
                    {
                        _serverIp = new byte[] { packet[optIdx + 2], packet[optIdx + 3], packet[optIdx + 4], packet[optIdx + 5] };
                    }

                    optIdx += 2 + len;
                }

                if (msgType == 2) // DHCP Offer
                {
                    _offeredIp = yiaddr;
                    _offerReceived = true;
                }
                else if (msgType == 5) // DHCP ACK
                {
                    _offeredIp = yiaddr;
                    _ackReceived = true;
                }
            }
            else
            {
                _offeredIp = yiaddr;
                _offerReceived = true;
            }
        }

        private byte[] BuildDhcpPacket(byte msgType, byte[] requestedIp, byte[] serverIp)
        {
            byte[] packet = new byte[342];
            byte[] mac = _driver.MACAddress.bytes;

            // 1. Ethernet Header (14 bytes)
            for (int i = 0; i < 6; i++) packet[i] = 0xFF; // Broadcast
            for (int i = 0; i < 6; i++) packet[6 + i] = mac[i]; // Source MAC
            packet[12] = 0x08; packet[13] = 0x00; // IPv4

            // 2. IPv4 Header (20 bytes)
            packet[14] = 0x45;
            packet[15] = 0x00;
            packet[16] = 0x01; packet[17] = 0x48; // Total Length (328)
            packet[18] = 0x00; packet[19] = 0x00;
            packet[20] = 0x00; packet[21] = 0x00;
            packet[22] = 0x40; // TTL: 64
            packet[23] = 17;   // Protocol: UDP
            packet[24] = 0x00; packet[25] = 0x00;
            packet[30] = 0xFF; packet[31] = 0xFF; packet[32] = 0xFF; packet[33] = 0xFF; // Broadcast
            
            CalculateIPv4Checksum(packet, 14);

            // 3. UDP Header (8 bytes)
            packet[34] = 0x00; packet[35] = 68; // Source Port 68
            packet[36] = 0x00; packet[37] = 67; // Destination Port 67
            packet[38] = 0x01; packet[39] = 0x34; // UDP Length 308
            packet[40] = 0x00; packet[41] = 0x00;

            // 4. DHCP Payload (300 bytes)
            packet[42] = 1; // BootRequest
            packet[43] = 1; // Ethernet
            packet[44] = 6; // MAC length
            packet[45] = 0; // Hops
            
            // XID: 0x12345678
            packet[46] = 0x12; packet[47] = 0x34; packet[48] = 0x56; packet[49] = 0x78;
            packet[50] = 0x00; packet[51] = 0x00;
            packet[52] = 0x80; packet[53] = 0x00; // Broadcast flag

            // Client Hardware Address at offset 70
            for (int i = 0; i < 6; i++) packet[70 + i] = mac[i];

            // Magic Cookie at offset 278
            packet[278] = 0x63; packet[279] = 0x82; packet[280] = 0x53; packet[281] = 0x63;

            int opt = 282;

            // Option 53: DHCP Message Type (1 = Discover, 3 = Request)
            packet[opt++] = 53;
            packet[opt++] = 1;
            packet[opt++] = msgType;

            // Option 61: Client Identifier
            packet[opt++] = 61;
            packet[opt++] = 7;
            packet[opt++] = 1; // Ethernet
            for (int i = 0; i < 6; i++) packet[opt++] = mac[i];

            // In DHCP Request: add Option 50 (Requested IP) & Option 54 (Server Identifier)
            if (msgType == 3 && requestedIp != null)
            {
                packet[opt++] = 50; packet[opt++] = 4;
                packet[opt++] = requestedIp[0]; packet[opt++] = requestedIp[1];
                packet[opt++] = requestedIp[2]; packet[opt++] = requestedIp[3];

                if (serverIp != null && serverIp[0] != 0)
                {
                    packet[opt++] = 54; packet[opt++] = 4;
                    packet[opt++] = serverIp[0]; packet[opt++] = serverIp[1];
                    packet[opt++] = serverIp[2]; packet[opt++] = serverIp[3];
                }
            }

            // Option 55: Parameter Request List (1=Subnet, 3=Router, 6=DNS)
            packet[opt++] = 55;
            packet[opt++] = 3;
            packet[opt++] = 1;
            packet[opt++] = 3;
            packet[opt++] = 6;

            // End Option
            packet[opt] = 255;

            return packet;
        }

        private void CalculateIPv4Checksum(byte[] packet, int offset)
        {
            uint sum = 0;
            for (int i = 0; i < 20; i += 2)
            {
                sum += (uint)((packet[offset + i] << 8) | packet[offset + i + 1]);
            }
            while ((sum >> 16) != 0)
            {
                sum = (sum & 0xFFFF) + (sum >> 16);
            }
            sum = ~sum;
            packet[offset + 10] = (byte)((sum >> 8) & 0xFF);
            packet[offset + 11] = (byte)(sum & 0xFF);
        }
    }
}
