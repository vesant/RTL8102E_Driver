using System;
using Cosmos.HAL.Network;
using Cosmos.System.Network.IPv4;

namespace RTL8102E_Driver
{
    public class CustomDHCP
    {
        private RTL8102E _driver;
        private bool _offerReceived = false;
        private byte[] _offeredIp = null;
        private byte[] _serverIp = null;

        public CustomDHCP(RTL8102E driver)
        {
            _driver = driver;
        }

        public byte[] DoAutoConfig()
        {
            Console.WriteLine("[DHCP] Starting Raw DHCP Discover...");
            
            // Hijack DataReceived
            var oldHandler = _driver.DataReceived;
            _driver.DataReceived = CustomDataReceived;

            // Craft raw DHCP Discover Packet
            byte[] discoverPacket = BuildDhcpDiscover();

            // Send initial packet
            _driver.QueueBytes(discoverPacket, 0, discoverPacket.Length);
            Console.WriteLine("[DHCP] Discover sent. Polling for Offer...");

            // Poll using HackPoll until we get an offer or timeout
            long timeout = 100000000L; // Much larger timeout for Atom CPU
            long iter = 0;
            int retries = 0;
            
            while (!_offerReceived && iter < timeout)
            {
                _driver.HackPoll();
                iter++;
                
                // Re-transmit every 20 million iterations in case PHY was still negotiating or packet dropped
                if (iter % 20000000 == 0)
                {
                    retries++;
                    Console.WriteLine($"[DHCP] Re-sending Discover (Retry {retries})...");
                    _driver.QueueBytes(discoverPacket, 0, discoverPacket.Length);
                }
            }

            // Restore normal handler
            _driver.DataReceived = oldHandler;

            if (_offerReceived && _offeredIp != null)
            {
                Console.WriteLine($"[DHCP] SUCCESS! Received IP: {_offeredIp[0]}.{_offeredIp[1]}.{_offeredIp[2]}.{_offeredIp[3]}");
                return _offeredIp;
            }
            
            Console.WriteLine("[DHCP] TIMEOUT! No DHCP Offer received.");
            return null;
        }

        private void CustomDataReceived(byte[] packet)
        {
            if (packet.Length < 14) return;
            
            ushort etherType = (ushort)((packet[12] << 8) | packet[13]);
            if (etherType == 0x0806)
            {
                Console.WriteLine($"[RX] ARP Packet length {packet.Length}");
                return; // ignore ARP for now
            }
            
            if (etherType != 0x0800) return; // Not IPv4
            
            if (packet.Length < 34) return;
            byte protocol = packet[23];
            
            if (protocol != 17) 
            {
                // Not UDP
                return;
            }

            ushort srcPort = (ushort)((packet[34] << 8) | packet[35]);
            ushort dstPort = (ushort)((packet[36] << 8) | packet[37]);
            
            Console.WriteLine($"[RX] UDP Packet {srcPort}->{dstPort} (Len: {packet.Length})");

            if (dstPort != 68) return;

            // Check DHCP Op (2 = BootReply)
            if (packet[42] != 2) return;

            // Check XID (0x12345678)
            if (packet[46] != 0x12 || packet[47] != 0x34 || packet[48] != 0x56 || packet[49] != 0x78) return;

            // Extract Your (Client) IP Address (YIAddr) at offset 58
            _offeredIp = new byte[] { packet[58], packet[59], packet[60], packet[61] };
            _serverIp = new byte[] { packet[62], packet[63], packet[64], packet[65] }; // Next Server IP
            
            _offerReceived = true;
        }

        private byte[] BuildDhcpDiscover()
        {
            byte[] packet = new byte[342];
            byte[] mac = _driver.MACAddress.bytes;

            // 1. Ethernet Header (14 bytes)
            for (int i = 0; i < 6; i++) packet[i] = 0xFF; // Dst MAC: Broadcast
            for (int i = 0; i < 6; i++) packet[6 + i] = mac[i]; // Src MAC
            packet[12] = 0x08; packet[13] = 0x00; // EtherType: IPv4

            // 2. IPv4 Header (20 bytes)
            packet[14] = 0x45; // Version/IHL
            packet[15] = 0x00; // TOS
            packet[16] = 0x01; packet[17] = 0x48; // Total Length: 328
            packet[18] = 0x00; packet[19] = 0x00; // ID
            packet[20] = 0x00; packet[21] = 0x00; // Flags/Frag
            packet[22] = 0x40; // TTL: 64
            packet[23] = 17;   // Protocol: UDP
            packet[24] = 0x00; packet[25] = 0x00; // Checksum (0 for now)
            // Src IP: 0.0.0.0 (already 0)
            packet[30] = 0xFF; packet[31] = 0xFF; packet[32] = 0xFF; packet[33] = 0xFF; // Dst IP: 255.255.255.255
            
            CalculateIPv4Checksum(packet, 14);

            // 3. UDP Header (8 bytes)
            packet[34] = 0x00; packet[35] = 68; // Src Port: 68
            packet[36] = 0x00; packet[37] = 67; // Dst Port: 67
            packet[38] = 0x01; packet[39] = 0x34; // UDP Length: 308
            packet[40] = 0x00; packet[41] = 0x00; // UDP Checksum (0 = disabled)

            // 4. DHCP Payload (300 bytes)
            packet[42] = 1; // Op: BootRequest
            packet[43] = 1; // HType: Ethernet
            packet[44] = 6; // HLen: MAC length
            packet[45] = 0; // Hops
            
            // XID: 0x12345678
            packet[46] = 0x12; packet[47] = 0x34; packet[48] = 0x56; packet[49] = 0x78;
            
            packet[50] = 0x00; packet[51] = 0x00; // Secs
            packet[52] = 0x80; packet[53] = 0x00; // Flags: Broadcast

            // CHAddr (Client Hardware Address) at offset 70
            for (int i = 0; i < 6; i++) packet[70 + i] = mac[i];

            // Magic Cookie at offset 278
            packet[278] = 0x63; packet[279] = 0x82; packet[280] = 0x53; packet[281] = 0x63;

            // Option 53: DHCP Message Type (Discover = 1)
            packet[282] = 53;
            packet[283] = 1;
            packet[284] = 1; // Discover

            // Option 61: Client Identifier
            packet[285] = 61; // Option 61
            packet[286] = 7;  // Length
            packet[287] = 1;  // Hardware type (Ethernet)
            for (int i = 0; i < 6; i++) packet[288 + i] = mac[i];

            // Option 55: Parameter Request List
            packet[294] = 55; // Option 55
            packet[295] = 3;  // Length
            packet[296] = 1;  // Subnet Mask
            packet[297] = 3;  // Router (Gateway)
            packet[298] = 6;  // DNS

            // End Option
            packet[299] = 255;

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
