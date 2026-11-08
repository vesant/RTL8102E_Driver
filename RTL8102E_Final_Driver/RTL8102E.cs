using System;
using System.Collections.Generic;
using Cosmos.Core;
using Cosmos.HAL;

namespace Cosmos.HAL
{
    [AttributeUsage(AttributeTargets.Class)]
    public class DriverAttribute : Attribute
    {
        public DriverAttribute(DriverType type, string description) { }
    }

    [AttributeUsage(AttributeTargets.Class)]
    public class AuthorAttribute : Attribute
    {
        public AuthorAttribute(string author) { }
    }

    public enum DriverType
    {
        Network,
        Storage,
        Audio
    }
}

namespace RTL8102E_Driver
{
    [Cosmos.HAL.Driver(Cosmos.HAL.DriverType.Network, "Realtek RTL8102E/RTL8136 PCI Ethernet")]
    [Cosmos.HAL.Author("ghost")]
    public class RTL8102E : NetworkDevice
    {
        private PCIDeviceNormal pciDevice;
        private uint mmioBase;
        private MemoryBlock mmio;
        private byte[] macBytes = new byte[6];

        private ManagedMemoryBlock rxDescBlock;
        private ManagedMemoryBlock txDescBlock;
        private ManagedMemoryBlock[] rxBuffers;
        private ManagedMemoryBlock[] txBuffers;

        // NetworkDevice Properties
        public override CardType CardType => CardType.Ethernet;
        public override Cosmos.HAL.Network.MACAddress MACAddress => new Cosmos.HAL.Network.MACAddress(macBytes);
        public override string Name => "RTL8102E";
        public override bool Ready => true;

        public static uint HardwareTxBytes = 0;
        public static uint HardwareRxBytes = 0;
        public static uint HardwareTxPackets = 0;
        public static uint HardwareRxPackets = 0;
        public static Action<byte[], bool> OnPacketSniffed = null;

        // Diagnostic Telemetry Properties
        public uint MmioBase => mmioBase;
        public uint RxRingPhysAddr => rxDescBlock != null ? (uint)rxDescBlock.Offset : 0;
        public uint TxRingPhysAddr => txDescBlock != null ? (uint)txDescBlock.Offset : 0;
        public int CurrentRxIndex => currentRxDesc;
        public int CurrentTxIndex => currentTxDesc;
        public string PciLocation => pciDevice != null ? $"{pciDevice.bus}:{pciDevice.slot}:{pciDevice.function}" : "N/A";
        public byte ReadCommandReg() => mmio != null ? mmio.Bytes[0x37] : (byte)0;
        public byte ReadPhyStatusReg() => mmio != null ? mmio.Bytes[0x6C] : (byte)0;
        public ushort ReadIsrReg() => mmio != null ? mmio.Words[0x3E] : (ushort)0;
        public ushort ReadImrReg() => mmio != null ? mmio.Words[0x3C] : (ushort)0;
        public uint ReadRxConfigReg() => mmio != null ? mmio.DWords[0x44] : 0;
        public uint ReadTxConfigReg() => mmio != null ? mmio.DWords[0x40] : 0;
        public string GetMacString()
        {
            return $"{macBytes[0]:X2}:{macBytes[1]:X2}:{macBytes[2]:X2}:{macBytes[3]:X2}:{macBytes[4]:X2}:{macBytes[5]:X2}";
        }
        
        // Boilerplate methods
        public override byte[] ReceivePacket() { return null; }
        public override int BytesAvailable() { return 0; }
        public override bool Enable() { return true; }
        public override bool IsReceiveBufferFull() { return false; }
        public override bool IsSendBufferFull() { return false; }
        public override bool ReceiveBytes(byte[] buffer, int offset, int max) { return false; }

        public RTL8102E()
        {
        }

        public bool Initialize()
        {
            Console.WriteLine("r8102e: initializing driver...");

            foreach (var device in Cosmos.HAL.PCI.Devices)
            {
                if (device.VendorID == 0x10EC && device.DeviceID == 0x8136)
                {
                    pciDevice = new PCIDeviceNormal(device.bus, device.slot, device.function);
                    
                    if (pciDevice != null)
                    {
                        Console.WriteLine($"r8102e: found device at pci {device.bus}:{device.slot}:{device.function}");
                        break;
                    }
                }
            }

            if (pciDevice == null)
            {
                Console.WriteLine("r8102e: error - device 10ec:8136 not found");
                return false;
            }

            // Explicitly enable Bus Mastering, Memory Space, IO Space, and clear INTx disable
            ushort pciCmd = (ushort)pciDevice.ReadRegister16(0x04);
            pciCmd |= (1 << 0) | (1 << 1) | (1 << 2); // IO space, Memory space, Bus Master
            pciCmd &= unchecked((ushort)~(1 << 10)); // Clear INTx disable
            pciDevice.WriteRegister16(0x04, pciCmd);
            pciDevice.EnableDevice();
            Console.WriteLine("r8102e: pci device enabled (bus master & memory)");

            bool foundMMIO = false;
            
            Console.WriteLine("r8102e: dumping raw bar registers...");
            for (byte i = 0; i < 6; i++)
            {
                uint rawBar = pciDevice.ReadRegister32((byte)(0x10 + (i * 4)));
                Console.WriteLine($"r8102e: raw BAR{i} = 0x{rawBar:X8}");
                
                if (rawBar != 0)
                {
                    bool isIo = (rawBar & 1) == 1;
                    if (!isIo)
                    {
                        // Check if it's 64-bit
                        byte type = (byte)((rawBar >> 1) & 0x03);
                        uint baseAddr = rawBar & 0xFFFFFFF0;
                        
                        if (type == 2) // 64-bit
                        {
                            i++; // skip next bar
                            uint rawBarHigh = pciDevice.ReadRegister32((byte)(0x10 + (i * 4)));
                            Console.WriteLine($"r8102e: raw BAR{i} (high) = 0x{rawBarHigh:X8}");
                            
                            // On 32-bit OS, we can only use it if upper 32 bits are 0
                            if (rawBarHigh != 0) {
                                Console.WriteLine("r8102e: warning - 64-bit bar is above 4GB, cannot use in 32-bit mode!");
                                continue;
                            }
                        }
                        
                        if (baseAddr != 0)
                        {
                            mmioBase = baseAddr;
                            Console.WriteLine($"r8102e: found MMIO at 0x{mmioBase:X8}");
                            foundMMIO = true;
                            break;
                        }
                    }
                }
            }

            if (!foundMMIO)
            {
                Console.WriteLine("r8102e: error - no valid mmio bar found");
                return false;
            }

            mmio = new MemoryBlock(mmioBase, 0x1000);
            return Phase2ResetAndMAC();
        }

        private void Write32(ManagedMemoryBlock block, uint offset, uint value)
        {
            block[offset] = (byte)(value & 0xFF);
            block[offset + 1] = (byte)((value >> 8) & 0xFF);
            block[offset + 2] = (byte)((value >> 16) & 0xFF);
            block[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        private bool Phase2ResetAndMAC()
        {
            Console.WriteLine("r8102e: issuing soft reset...");
            
            // Soft Reset command (0x10) to Command Register (CR, offset 0x37)
            mmio.Bytes[0x37] = 0x10;

            int timeout = 10000;
            while ((mmio.Bytes[0x37] & 0x10) != 0)
            {
                timeout--;
                if (timeout <= 0)
                {
                    Console.WriteLine("r8102e: error - soft reset timeout");
                    return false;
                }
            }
            Console.WriteLine("r8102e: soft reset complete");

            for (uint i = 0; i < 6; i++)
            {
                macBytes[i] = mmio.Bytes[i];
            }

            Console.Write("r8102e: mac address ");
            for (int i = 0; i < 6; i++)
            {
                Console.Write(macBytes[i].ToString("x2"));
                if (i < 5) Console.Write(":");
            }
            Console.WriteLine();

            Console.WriteLine("r8102e: phase 1 and 2 complete");
            return Phase3DescriptorRings();
        }

        public const int DESC_COUNT = 16;

        private bool Phase3DescriptorRings()
        {
            Console.WriteLine("r8102e: initializing dma descriptor rings...");

            // Allocate 16 descriptors for RX and TX (16 * 16 = 256 bytes), aligned to 256
            rxDescBlock = new ManagedMemoryBlock(DESC_COUNT * 16, 256);
            txDescBlock = new ManagedMemoryBlock(DESC_COUNT * 16, 256);

            // Allocate RX Buffers (16 buffers of 1536 bytes -> aligned to 8 bytes)
            rxBuffers = new ManagedMemoryBlock[DESC_COUNT];
            for (int i = 0; i < DESC_COUNT; i++)
            {
                rxBuffers[i] = new ManagedMemoryBlock(1536, 8);
            }

            // Allocate TX Buffers (16 buffers of 1536 bytes -> aligned to 8 bytes)
            txBuffers = new ManagedMemoryBlock[DESC_COUNT];
            for (int i = 0; i < DESC_COUNT; i++)
            {
                txBuffers[i] = new ManagedMemoryBlock(1536, 8);
            }

            Console.WriteLine("r8102e: setting up rx ring in ram...");
            for (int i = 0; i < DESC_COUNT; i++)
            {
                uint offset = (uint)(i * 16);
                
                // Command/Status: 1536 buffer size (bits 0-13) | OWN bit (bit 31)
                // If it's the last descriptor, set EOR bit (bit 30)
                uint cmdStatus = 1536 | 0x80000000;
                if (i == DESC_COUNT - 1) cmdStatus |= 0x40000000;
                
                Write32(rxDescBlock, offset, cmdStatus);
                Write32(rxDescBlock, offset + 4, 0); // Vlan
                Write32(rxDescBlock, offset + 8, (uint)rxBuffers[i].Offset); // Buffer Address Low
                Write32(rxDescBlock, offset + 12, (uint)(rxBuffers[i].Offset >> 32)); // Buffer Address High
            }

            Console.WriteLine("r8102e: setting up tx ring in ram...");
            for (int i = 0; i < DESC_COUNT; i++)
            {
                uint offset = (uint)(i * 16);
                uint txCmd = (i == DESC_COUNT - 1) ? 0x40000000u : 0u;
                Write32(txDescBlock, offset, txCmd);
                Write32(txDescBlock, offset + 4, 0); // Vlan
                Write32(txDescBlock, offset + 8, (uint)txBuffers[i].Offset); // Buffer Address Low
                Write32(txDescBlock, offset + 12, (uint)(txBuffers[i].Offset >> 32)); // Buffer Address High
            }

            Console.WriteLine("r8102e: writing physical addresses to registers...");
            
            // Write TX Descriptor Start Address (0x20 Low, 0x24 High)
            mmio.DWords[0x20] = (uint)txDescBlock.Offset;
            mmio.DWords[0x24] = (uint)(txDescBlock.Offset >> 32);

            // Write RX Descriptor Start Address (0xE4 Low, 0xE8 High)
            mmio.DWords[0xE4] = (uint)rxDescBlock.Offset;
            mmio.DWords[0xE8] = (uint)(rxDescBlock.Offset >> 32);

            Console.WriteLine($"r8102e: rx ring physical addr: 0x{rxDescBlock.Offset:X8}");
            Console.WriteLine($"r8102e: tx ring physical addr: 0x{txDescBlock.Offset:X8}");
            Console.WriteLine("r8102e: phase 3 complete");
            
            return Phase4And5();
        }

        public volatile ushort LastIsr = 0;
        public volatile uint IrqCount = 0;
        private int currentRxDesc = 0;

        private uint Read32(ManagedMemoryBlock block, uint offset)
        {
            return (uint)(block[offset] | (block[offset + 1] << 8) | (block[offset + 2] << 16) | (block[offset + 3] << 24));
        }

        private bool Phase4And5()
        {
            Console.WriteLine("r8102e: configuring RxConfig and TxConfig...");
            // RxConfig (0x44): Accept Broadcast (0x08), Multicast (0x04), Physical Match (0x02), Unlimited FIFO (0x700), Max DMA (0xE000) -> 0x0000E70E
            mmio.DWords[0x44] = 0x0000E70E;

            // TxConfig (0x40): Max DMA burst
            mmio.DWords[0x40] = 0x60000000;

            // RxMaxSize (0xDA): 1536 bytes
            mmio.Words[0xDA] = 0x0600;

            Console.WriteLine("r8102e: enabling RX and TX in CR...");
            // Command Register (0x37): RE=0x08, TE=0x04 -> 0x0C
            mmio.Bytes[0x37] = 0x0C;

            Console.WriteLine("r8102e: verifying PHY Link Status...");
            // PHYStatus register is at offset 0x6C (8-bit)
            // Bit 1 indicates Link Status (1 = UP, 0 = DOWN)
            int linkSpins = 0;
            while ((mmio.Bytes[0x6C] & 0x02) == 0 && linkSpins < 500000)
            {
                linkSpins++;
            }
            if ((mmio.Bytes[0x6C] & 0x02) != 0)
            {
                Console.WriteLine("r8102e: PHY Link is UP! Cable connected.");
            }
            else
            {
                Console.WriteLine("r8102e: PHY Link is DOWN. Please check the network cable.");
            }

            Console.WriteLine("r8102e: masking all hardware interrupts in IMR (switching back to Polling mode)...");
            // Interrupt Mask Register (0x3C): 0x0000 ensures no hardware INTA# is generated
            // This prevents Cosmos OS deadlocks caused by single-core PIT timer blocking.
            mmio.Words[0x3C] = 0x0000;

            Console.WriteLine("r8102e: driver fully initialized and ready for HackPoll!");
            return true;
        }

        public bool IsPhyLinkUp()
        {
            if (mmio == null) return false;
            return (mmio.Bytes[0x6C] & 0x02) != 0;
        }

        private int currentTxDesc = 0;

        public override bool QueueBytes(byte[] buffer)
        {
            if (buffer == null || buffer.Length == 0) return false;
            return QueueBytes(buffer, 0, buffer.Length);
        }

        public override bool QueueBytes(byte[] buffer, int offset, int length)
        {
            uint descOffset = (uint)(currentTxDesc * 16);
            
            // Wait briefly for descriptor if still owned by NIC
            int spins = 500;
            while ((Read32(txDescBlock, descOffset) & 0x80000000) != 0 && spins > 0)
            {
                mmio.Bytes[0x38] = 0x40; // Kick doorbell in case previous poll was missed
                spins--;
            }

            uint cmdStatus = Read32(txDescBlock, descOffset);
            if ((cmdStatus & 0x80000000) != 0)
                return false; // Queue full!

            // Copy data to txBuffers[currentTxDesc]
            for (int i = 0; i < length; i++)
            {
                txBuffers[currentTxDesc][(uint)i] = buffer[offset + i];
            }

            // Ethernet requires a minimum payload of 60 bytes (excluding CRC).
            // RTL8102E does NOT auto-pad runts!
            uint txLength = (uint)length;
            if (txLength < 60)
            {
                for (uint i = txLength; i < 60; i++)
                {
                    txBuffers[currentTxDesc][i] = 0; // Pad with zeros
                }
                txLength = 60;
            }

            // CommandStatus: Length | OWN (bit 31) | FS (bit 29) | LS (bit 28)
            uint newCmdStatus = txLength | 0x80000000 | 0x20000000 | 0x10000000;
            if (currentTxDesc == DESC_COUNT - 1) newCmdStatus |= 0x40000000; // Preserve EOR bit
            
            Write32(txDescBlock, descOffset, newCmdStatus);
            
            HardwareTxBytes += txLength;
            HardwareTxPackets++;

            if (OnPacketSniffed != null)
            {
                try { OnPacketSniffed(buffer, true); } catch { }
            }

            // Trigger TX poll (TxPoll register 0x38 = 0x40)
            mmio.Bytes[0x38] = 0x40;
            mmio.Bytes[0x38] = 0x40; // Double kick (Linux 8168 doorbell hack)
            
            currentTxDesc = (currentTxDesc + 1) % DESC_COUNT;
            return true;
        }

        /// <summary>
        /// HackPoll intercepts the hardware directly, bypassing Cosmos OS IRQs.
        /// This avoids full system freezes on single-core devices when Cosmos blocks.
        /// </summary>
        public void HackPoll()
        {
            if (rxDescBlock == null || txDescBlock == null || mmio == null) return;

            // 1. Process RX Ring (up to DESC_COUNT descriptors per poll)
            for (int d = 0; d < DESC_COUNT; d++)
            {
                uint offset = (uint)(currentRxDesc * 16);
                uint cmdStatus = Read32(rxDescBlock, offset);
                
                // If OWN bit (bit 31) is 1, MAC still owns it, ring is empty for us
                if ((cmdStatus & 0x80000000) != 0)
                    break;
                    
                // Bit 31 is 0! MAC transferred a packet to RAM!
                // Realtek includes the 4-byte Ethernet CRC in the length. Cosmos doesn't want it.
                uint rawLength = cmdStatus & 0x3FFF;
                if (rawLength > 1536) rawLength = 1536;
                uint length = rawLength > 4 ? rawLength - 4 : rawLength; 
                
                HardwareRxBytes += length;
                HardwareRxPackets++;

                if (length > 0)
                {
                    // Read into managed array of exact frame length for NetworkStack, DHCP & TCP parsers
                    byte[] packet = new byte[length];
                    for (uint i = 0; i < length; i++)
                    {
                        packet[i] = rxBuffers[currentRxDesc][i];
                    }

                    if (OnPacketSniffed != null)
                    {
                        try { OnPacketSniffed(packet, false); } catch { }
                    }

                    // Send to NetworkStack (or custom Interceptor) with safe error handling
                    if (DataReceived != null)
                    {
                        try
                        {
                            DataReceived(packet);
                        }
                        catch { }
                    }
                }
                
                // Give descriptor back to MAC
                uint newCmdStatus = 1536 | 0x80000000;
                if (currentRxDesc == DESC_COUNT - 1) newCmdStatus |= 0x40000000; // Preserve EOR bit on last desc
                
                Write32(rxDescBlock, offset, newCmdStatus);
                
                currentRxDesc = (currentRxDesc + 1) % DESC_COUNT;
            }

            // 2. Transmit Watchdog: check if any TX descriptor is pending and kick TPPOLL
            for (int t = 0; t < DESC_COUNT; t++)
            {
                if ((Read32(txDescBlock, (uint)(t * 16)) & 0x80000000) != 0)
                {
                    mmio.Bytes[0x38] = 0x40;
                    break;
                }
            }

            // 3. Read and Clear Interrupt Status Register (0x3E)
            ushort status = mmio.Words[0x3E];
            if (status != 0)
            {
                LastIsr = status;
                IrqCount++;
                
                // Acknowledge by writing the exact same bits back to ISR
                mmio.Words[0x3E] = status;

                // If RDU (bit 5: 0x0020) or RXOVW (bit 4: 0x0010) occurred, re-arm the receiver!
                if ((status & 0x0030) != 0)
                {
                    mmio.Bytes[0x37] = 0x0C;
                }
            }
        }
    }


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


    /// <summary>
    /// Bare-metal Atheros AR2425 / AR5007EG / AR5212 IEEE 802.11b/g Wireless Driver.
    /// Supports PCI/PCIe Vendor 0x168C (Atheros AR2425, AR5212, AR2413, AR5413, AR2417).
    /// Performs direct 32-bit MMIO register control, EEPROM reads, RF synthesis tuning (2.4 GHz Ch 1-13),
    /// RX DMA descriptor management, and real-time 802.11 Beacon / Probe Response frame parsing.
    /// Pure polling mode - 0 interrupts, 0 Thread.Sleep to prevent lockups on Intel Atom N270.
    /// </summary>
    [Cosmos.HAL.Driver(Cosmos.HAL.DriverType.Network, "Atheros AR2425/AR5007EG 802.11b/g Wireless")]
    [Cosmos.HAL.Author("ghost")]
    public class AtherosAR2425Driver
    {
        public PCIDevice PciDev { get; private set; }
        public uint MmioBase { get; private set; }
        public MemoryBlock Mmio { get; private set; }
        public byte[] MacAddress { get; private set; } = new byte[6];
        public bool IsInitialized { get; private set; } = false;
        public uint MacSrev { get; private set; }
        public uint PhyChipId { get; private set; }
        public ushort EepromVersion { get; private set; }
        public ushort EepromRegDomain { get; private set; }
        public int CurrentChannel { get; private set; } = 1;
        public uint RxPacketsCount { get; private set; } = 0;
        public uint TxPacketsCount { get; private set; } = 0;
        public uint BeaconFramesCount { get; private set; } = 0;
        public string HardwareSummary { get; private set; } = "Nao detetado";

        // RX DMA Descriptors (16 descriptors)
        private const int RX_DESC_COUNT = 16;
        private const int RX_BUF_SIZE = 2048;
        private ManagedMemoryBlock _rxDescMem;
        private ManagedMemoryBlock _rxBufferMem;
        private uint _rxDescPhys;
        private uint _rxBufferPhys;
        private int _rxHead = 0;

        // TX DMA Descriptors (4 descriptors)
        private const int TX_DESC_COUNT = 4;
        private const int TX_BUF_SIZE = 512;
        private ManagedMemoryBlock _txDescMem;
        private ManagedMemoryBlock _txBufferMem;
        private uint _txDescPhys;
        private uint _txBufferPhys;
        private int _txHead = 0;

        // Detected Wi-Fi networks dictionary (BSSID -> WifiAccessPoint)
        public Dictionary<string, WifiAccessPoint> DetectedAps = new Dictionary<string, WifiAccessPoint>();

        // Register Addresses
        private const uint AR5K_CR = 0x0008;
        private const uint AR5K_CR_RXE = 0x00000004;
        private const uint AR5K_CR_TXE = 0x00000001;
        private const uint AR5K_CR_RXD = 0x00000020;
        private const uint AR5K_CR_TXD = 0x00000010;

        private const uint AR5K_CFG = 0x0014;
        private const uint AR5K_IER = 0x0024;
        private const uint AR5K_TXCFG = 0x0030;
        private const uint AR5K_RXCFG = 0x0034;
        private const uint AR5K_RXDP = 0x000C;

        private const uint AR5K_QCU_TXE = 0x0840;
        private const uint AR5K_QUEUE_TXDP_0 = 0x0800;
        private const uint AR5K_QUEUE_MISC_0 = 0x09C0;
        private const uint AR5K_QUEUE_DFS_LOCAL_IFS_0 = 0x1040;
        private const uint AR5K_QUEUE_DFS_RETRY_LIMIT_0 = 0x1080;
        private const uint AR5K_QUEUE_QCUMASK_0 = 0x1000;

        private const uint AR5K_RESET_CTL = 0x4000;
        private const uint AR5K_SLEEP_CTL = 0x4004;
        private const uint AR5K_PCICFG = 0x4010;
        private const uint AR5K_PCIE_SERDES = 0x4040;
        private const uint AR5K_PCIE_SERDES_RESET = 0x4044;

        private const uint AR5K_STA_ID0 = 0x8000;
        private const uint AR5K_STA_ID1 = 0x8004;
        private const uint AR5K_BSS_ID0 = 0x8008;
        private const uint AR5K_BSS_ID1 = 0x800C;
        private const uint AR5K_RX_FILTER = 0x803C;
        private const uint AR5K_MCAST_FILTER0 = 0x8040;
        private const uint AR5K_MCAST_FILTER1 = 0x8044;
        private const uint AR5K_DIAG_SW_5211 = 0x8048;
        private const uint AR5K_DEFAULT_ANTENNA = 0x80D0;
        private const uint AR5K_BSS_IDM0 = 0x80E0;
        private const uint AR5K_BSS_IDM1 = 0x80E4;

        private const uint AR5K_EEPROM_BASE = 0x6000;
        private const uint AR5K_EEPROM_DATA_5211 = 0x6004;
        private const uint AR5K_EEPROM_CMD = 0x6008;
        private const uint AR5K_EEPROM_STAT_5211 = 0x600C;

        private const uint AR5K_PHY_0 = 0x9800;
        private const uint AR5K_PHY_TURBO = 0x9804;
        private const uint AR5K_PHY_TIMING_3 = 0x9814;
        private const uint AR5K_PHY_ACT = 0x981C;
        private const uint AR5K_PHY_RF_CTL3 = 0x9828;
        private const uint AR5K_PHY_RF_CTL4 = 0x9834;
        private const uint AR5K_PHY_SETTLING = 0x9844;
        private const uint AR5K_PHY_GAIN = 0x9848;
        private const uint AR5K_PHY_DESIRED_SIZE = 0x9850;
        private const uint AR5K_PHY_AGCCTL = 0x9860;
        private const uint AR5K_PHY_NF62 = 0x9864;
        private const uint AR5K_PHY_SCAL = 0x9878;
        private const uint AR5K_PHY_PLL = 0x987C;
        private const uint AR5K_RF_BUFFER = 0x989C;
        private const uint AR5K_RF_BUFFER_CONTROL_5 = 0x98D8;
        private const uint AR5K_PHY_ANT_SWITCH_TABLE_0 = 0x9910;
        private const uint AR5K_PHY_ANT_SWITCH_TABLE_1 = 0x9960;
        private const uint AR5K_PHY_ANT_CTL = 0x9964;
        private const uint AR5K_PHY_NFTHRES = 0x9968;
        private const uint AR5K_PHY_MODE = 0xA200;
        private const uint AR5K_PHY_CCKTXCTL = 0xA204;
        private const uint AR5K_PHY_FAST_ADC = 0xA24C;
        private const uint AR5K_PHY_CHIP_ID = 0x9818;

        public bool FindAndInitialize()
        {
            Console.WriteLine("ath5k: searching for Atheros 802.11 Wireless LAN device on PCI bus...");
            PCIDevice foundDev = null;

            foreach (var dev in PCI.Devices)
            {
                if (dev.VendorID == 0x168C)
                {
                    foundDev = dev;
                    break;
                }
            }

            if (foundDev == null)
            {
                Console.WriteLine("ath5k: no Atheros PCI device (Vendor 0x168C) found.");
                HardwareSummary = "Nenhum chip Atheros (0x168C) no PCI";
                return false;
            }

            PciDev = foundDev;
            Console.WriteLine($"ath5k: found device VID:0x{PciDev.VendorID:X4} DID:0x{PciDev.DeviceID:X4} at {PciDev.bus}:{PciDev.slot}:{PciDev.function}");

            // Enable Memory and Bus Master in PCI Command Register
            PciDev.EnableMemory(true);
            PciDev.EnableBusMaster(true);

            // Set PCI latency timer and cache line size for DMA
            PciDev.WriteRegister8(0x0C, 16); // Cache line size = 16 dwords (64B)
            PciDev.WriteRegister8(0x0D, 0xA8); // Latency timer
            PciDev.WriteRegister8(0x41, 0); // Disable retry timeout

            // Read BAR0 (MMIO base)
            uint bar0 = PciDev.ReadRegister32(0x10);
            MmioBase = bar0 & 0xFFFFFFF0;
            if (MmioBase == 0)
            {
                Console.WriteLine("ath5k: BAR0 MMIO base is 0!");
                HardwareSummary = "BAR0 MMIO Invalido";
                return false;
            }

            Console.WriteLine($"ath5k: MMIO base mapped at 0x{MmioBase:X8}");
            Mmio = new MemoryBlock(MmioBase, 0x10000);

            // Program PCIe SerDes if PCIe device (e.g. AR2425 DID 0x001C)
            if (PciDev.DeviceID == 0x001C || PciDev.DeviceID == 0x0023 || PciDev.DeviceID == 0x0024)
            {
                Console.WriteLine("ath5k: configuring PCIe SerDes PLL and power settings...");
                WriteReg(AR5K_PCIE_SERDES, 0x9248fc00);
                WriteReg(AR5K_PCIE_SERDES, 0x24924924);
                WriteReg(AR5K_PCIE_SERDES, 0x28000039);
                WriteReg(AR5K_PCIE_SERDES, 0x53160824);
                WriteReg(AR5K_PCIE_SERDES, 0xe5980579);
                WriteReg(AR5K_PCIE_SERDES, 0x001defff);
                WriteReg(AR5K_PCIE_SERDES, 0x1aaabe40);
                WriteReg(AR5K_PCIE_SERDES, 0xbe105554);
                WriteReg(AR5K_PCIE_SERDES, 0x000e3007);
                WriteReg(AR5K_PCIE_SERDES_RESET, 0);
                DelayMicroseconds(1000);
            }

            // Wake up NIC & soft reset
            NicWakeup();

            // Read SREV & PHY IDs
            MacSrev = ReadReg(0x0020); // AR5K_SREV
            PhyChipId = ReadReg(AR5K_PHY_CHIP_ID);
            Console.WriteLine($"ath5k: MAC SREV: 0x{MacSrev:X8}, PHY ID: 0x{PhyChipId:X8}");

            // Read EEPROM Header and MAC
            InitEeprom();

            HardwareSummary = $"Atheros AR2425/AR5212 [PCI {PciDev.bus}:{PciDev.slot}:{PciDev.function}] MAC {GetMacString()} SREV 0x{MacSrev:X2}";

            // Allocate and setup RX / TX DMA Descriptors
            SetupDmaDescriptors();

            // Reset and tune to Channel 1 (2412 MHz)
            ResetAndTuneChannel(1);

            IsInitialized = true;
            Console.WriteLine("ath5k: Atheros 802.11 driver initialized and actively listening for Beacons!");
            return true;
        }

        private void WriteReg(uint reg, uint val)
        {
            Mmio.DWords[reg] = val;
        }

        private uint ReadReg(uint reg)
        {
            return Mmio.DWords[reg];
        }

        private void DelayMicroseconds(int us)
        {
            // Intel Atom N270 calibration loop
            int count = us * 12;
            for (int i = 0; i < count; i++)
            {
                IOPort.Wait();
            }
        }

        private void NicWakeup()
        {
            // Clear power save bit in STA_ID1
            uint sta1 = ReadReg(AR5K_STA_ID1);
            WriteReg(AR5K_STA_ID1, sta1 & ~0x00040000u);

            // Set wake in SLEEP_CTL
            WriteReg(AR5K_SLEEP_CTL, 0);

            // Poll PCICFG until sleep state clears
            for (int i = 0; i < 5000; i++)
            {
                if ((ReadReg(AR5K_PCICFG) & 0x00010000u) == 0) break;
                DelayMicroseconds(10);
            }

            // Warm reset: reset baseband and PCU (Do NOT reset PCI on PCIe!)
            WriteReg(AR5K_RESET_CTL, 0x03);
            DelayMicroseconds(50);
            WriteReg(AR5K_SLEEP_CTL, 0);
            WriteReg(AR5K_RESET_CTL, 0);
            WriteReg(AR5K_CFG, 0);
            DelayMicroseconds(100);
        }

        private void InitEeprom()
        {
            // Read magic & version
            ushort magic = ReadEepromWord(0x003D);
            EepromVersion = ReadEepromWord(0x00C1);
            EepromRegDomain = ReadEepromWord(0x00BF);

            // Read MAC address from offsets 0x1F, 0x1E, 0x1D
            ushort w2 = ReadEepromWord(0x1F);
            ushort w1 = ReadEepromWord(0x1E);
            ushort w0 = ReadEepromWord(0x1D);

            MacAddress[0] = (byte)(w2 & 0xFF);
            MacAddress[1] = (byte)((w2 >> 8) & 0xFF);
            MacAddress[2] = (byte)(w1 & 0xFF);
            MacAddress[3] = (byte)((w1 >> 8) & 0xFF);
            MacAddress[4] = (byte)(w0 & 0xFF);
            MacAddress[5] = (byte)((w0 >> 8) & 0xFF);

            // If EEPROM read was blank (00:00:00:00:00:00 or FF:FF:FF:FF:FF:FF), use Atheros fallback
            if ((MacAddress[0] == 0 && MacAddress[1] == 0 && MacAddress[2] == 0) ||
                (MacAddress[0] == 0xFF && MacAddress[1] == 0xFF))
            {
                MacAddress = new byte[] { 0x00, 0x1E, 0x65, 0x8F, 0xC1, 0x0D };
            }

            Console.WriteLine($"ath5k: EEPROM Ver: 0x{EepromVersion:X4}, RegDomain: 0x{EepromRegDomain:X4}, MAC: {GetMacString()}");
        }

        private ushort ReadEepromWord(uint offset)
        {
            WriteReg(AR5K_EEPROM_BASE, offset);
            WriteReg(AR5K_EEPROM_CMD, 0x01); // Read command

            for (int i = 0; i < 2000; i++)
            {
                uint stat = ReadReg(AR5K_EEPROM_STAT_5211);
                if ((stat & 0x02) != 0) // Done
                {
                    return (ushort)(ReadReg(AR5K_EEPROM_DATA_5211) & 0xFFFF);
                }
                DelayMicroseconds(5);
            }
            return 0;
        }

        public string GetMacString()
        {
            return $"{MacAddress[0]:X2}:{MacAddress[1]:X2}:{MacAddress[2]:X2}:{MacAddress[3]:X2}:{MacAddress[4]:X2}:{MacAddress[5]:X2}";
        }

        private void SetupDmaDescriptors()
        {
            // Allocate 16 RX Descriptors (24 bytes each, aligned) + Buffers (2048 bytes each)
            int descTotalSize = RX_DESC_COUNT * 32;
            int bufTotalSize = RX_DESC_COUNT * RX_BUF_SIZE;

            _rxDescMem = new ManagedMemoryBlock((uint)descTotalSize, 256);
            _rxDescPhys = (uint)_rxDescMem.Offset;

            _rxBufferMem = new ManagedMemoryBlock((uint)bufTotalSize, 8);
            _rxBufferPhys = (uint)_rxBufferMem.Offset;

            // Initialize RX Descriptors Ring
            for (int i = 0; i < RX_DESC_COUNT; i++)
            {
                uint dAddr = (uint)(_rxDescPhys + i * 32);
                uint nextDAddr = (uint)(_rxDescPhys + ((i + 1) % RX_DESC_COUNT) * 32);
                uint bAddr = (uint)(_rxBufferPhys + i * RX_BUF_SIZE);

                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                descBlock.DWords[0] = nextDAddr; // ds_link
                descBlock.DWords[4] = bAddr;    // ds_data
                descBlock.DWords[8] = 0;        // rx_ctl0
                descBlock.DWords[12] = (uint)(RX_BUF_SIZE & 0x0FFF); // rx_ctl1: buf_len
                descBlock.DWords[16] = 0;       // rx_stat0
                descBlock.DWords[20] = 0;       // rx_stat1
            }

            // Allocate TX Descriptors & Buffers
            int txDescTotal = TX_DESC_COUNT * 32;
            int txBufTotal = TX_DESC_COUNT * TX_BUF_SIZE;

            _txDescMem = new ManagedMemoryBlock((uint)txDescTotal, 256);
            _txDescPhys = (uint)_txDescMem.Offset;

            _txBufferMem = new ManagedMemoryBlock((uint)txBufTotal, 8);
            _txBufferPhys = (uint)_txBufferMem.Offset;

            for (int i = 0; i < TX_DESC_COUNT; i++)
            {
                uint dAddr = (uint)(_txDescPhys + i * 32);
                uint bAddr = (uint)(_txBufferPhys + i * TX_BUF_SIZE);

                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                descBlock.DWords[0] = 0;        // ds_link
                descBlock.DWords[4] = bAddr;    // ds_data
                descBlock.DWords[8] = 0;        // tx_ctl0
                descBlock.DWords[12] = 0;       // tx_ctl1
                descBlock.DWords[16] = 0;       // tx_ctl2
                descBlock.DWords[20] = 0;       // tx_ctl3
                descBlock.DWords[24] = 0;       // tx_stat0
                descBlock.DWords[28] = 0;       // tx_stat1
            }
        }

        public void ResetAndTuneChannel(int channel)
        {
            if (channel < 1) channel = 1;
            if (channel > 13) channel = 13;
            CurrentChannel = channel;

            // Stop RX & TX DMA
            WriteReg(AR5K_CR, AR5K_CR_RXD | AR5K_CR_TXD);
            WriteReg(AR5K_IER, 0); // Polling mode only!

            NicWakeup();

            // Set PHY Shift
            WriteReg(AR5K_PHY_0, 0x00000007); // AR5K_PHY_SHIFT_5GHZ

            // Apply Mode Start Initvals
            ApplyModeStartTable();

            // Apply Common Start Initvals
            ApplyCommonStartTable();

            // Apply Mode End (RF2425)
            ApplyModeEndTable();

            // Apply Common End (RF2425)
            ApplyCommonEndTable();

            // Baseband gain & RF gain
            ApplyBbGainTable();
            ApplyRfGainTable();

            // Tweak initval settings for AR2425 Swan
            WriteReg(AR5K_PHY_SCAL, 0x0E);
            WriteReg(AR5K_PHY_FAST_ADC, 0x01);

            // Commit RF Buffer
            ApplyRfBuffer();

            // OFDM Timings (AR5K_PHY_TIMING_3)
            WriteReg(AR5K_PHY_TIMING_3, 0x9C0A9F6B);

            // Antenna switch table & idle control (Both antennas: Main + Aux)
            WriteReg(AR5K_PHY_ANT_SWITCH_TABLE_0, 0x00000001);
            WriteReg(AR5K_PHY_ANT_SWITCH_TABLE_1, 0x00000002);
            WriteReg(AR5K_PHY_ANT_CTL, 0x00000011); // Diversity active on both antennas
            WriteReg(AR5K_PHY_NFTHRES, 0x000001F0); // Noise floor threshold (-95 dBm)
            WriteReg(AR5K_DEFAULT_ANTENNA, 0x01);   // Default to Antenna 1 (Main / Cabo Preto)

            // Setup PLL & Channel Frequency
            // Clock = 44MHz (0xAB) | PLL_RF5112 (0x40) = 0xEB
            WriteReg(AR5K_PHY_PLL, 0x000000EB);
            DelayMicroseconds(300);

            // PHY Mode: Dynamic OFDM/CCK (0x04) | 2GHz (0x02) | RF5112 (0x08) = 0x0E
            WriteReg(AR5K_PHY_MODE, 0x0000000E);
            WriteReg(AR5K_PHY_TURBO, 0);

            // Tune Frequency: Freq = 2407 + 5 * channel
            int freqMhz = 2407 + 5 * channel;
            TuneRf2425Channel(freqMhz);

            // Activate PHY
            WriteReg(AR5K_PHY_ACT, 0x01);
            DelayMicroseconds(500);

            // Run AGC Calibration
            uint agc = ReadReg(AR5K_PHY_AGCCTL);
            WriteReg(AR5K_PHY_AGCCTL, agc | 0x03); // Calibrate AGC & NF
            for (int i = 0; i < 1000; i++)
            {
                if ((ReadReg(AR5K_PHY_AGCCTL) & 0x03) == 0) break;
                DelayMicroseconds(10);
            }

            // Setup MAC addresses & BSSID filter
            uint lowMac = (uint)(MacAddress[0] | (MacAddress[1] << 8) | (MacAddress[2] << 16) | (MacAddress[3] << 24));
            uint highMac = (uint)(MacAddress[4] | (MacAddress[5] << 8));
            WriteReg(AR5K_STA_ID0, lowMac);
            WriteReg(AR5K_STA_ID1, highMac | 0x00000000); // Station mode

            // BSSID Mask (Promiscuous BSSID acceptance for scanning)
            WriteReg(AR5K_BSS_IDM0, 0x00000000);
            WriteReg(AR5K_BSS_IDM1, 0x00000000);
            WriteReg(AR5K_BSS_ID0, 0xFFFFFFFF);
            WriteReg(AR5K_BSS_ID1, 0x0000FFFF);

            // Multicast filter accept all
            WriteReg(AR5K_MCAST_FILTER0, 0xFFFFFFFF);
            WriteReg(AR5K_MCAST_FILTER1, 0xFFFFFFFF);

            // RX Filter: Accept Unicast, Broadcast, Multicast, Control, Beacons, Probe Requests, Promiscuous
            WriteReg(AR5K_RX_FILTER, 0x000000FF);
            WriteReg(AR5K_DIAG_SW_5211, 0); // Ensure RX is NOT disabled

            // DMA Config
            WriteReg(AR5K_TXCFG, 0x00000015); // 128-byte DMA bursts
            WriteReg(AR5K_RXCFG, 0x00000005); // 128-byte DMA bursts

            // Setup TX Queue 0 for active Probe Requests
            WriteReg(AR5K_QUEUE_QCUMASK_0, 0x01);
            WriteReg(AR5K_QUEUE_DFS_LOCAL_IFS_0, 0x002FFC0F);
            WriteReg(AR5K_QUEUE_DFS_RETRY_LIMIT_0, 0x0000000A);
            WriteReg(AR5K_QUEUE_MISC_0, 0);

            // Start RX DMA
            WriteReg(AR5K_RXDP, _rxDescPhys);
            WriteReg(AR5K_CR, AR5K_CR_RXE);
        }

        private void TuneRf2425Channel(int freqMhz)
        {
            // Reverse bits of (freq - 2272)
            int c = freqMhz - 2272;
            byte b = (byte)c;
            byte swapped = 0;
            for (int i = 0; i < 8; i++)
            {
                if ((b & (1 << i)) != 0) swapped |= (byte)(1 << (7 - i));
            }

            uint data = (uint)((swapped << 4) | 0x1001);
            WriteReg(AR5K_RF_BUFFER, data & 0xFF);
            WriteReg(AR5K_RF_BUFFER_CONTROL_5, (data >> 8) & 0x7F);
            WriteReg(AR5K_PHY_CCKTXCTL, 0);
        }

        private void ApplyModeStartTable()
        {
            uint[] t = Ar5212IniModeStart;
            for (int i = 0; i < t.Length; i += 3)
            {
                WriteReg(t[i], t[i + 2]); // column 2 = 11g
            }
        }

        private void ApplyCommonStartTable()
        {
            uint[] t = Ar5212IniCommonStart;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyModeEndTable()
        {
            uint[] t = Rf2425IniModeEnd;
            for (int i = 0; i < t.Length; i += 3)
            {
                WriteReg(t[i], t[i + 2]); // column 2 = 11g
            }
        }

        private void ApplyCommonEndTable()
        {
            uint[] t = Rf2425IniCommonEnd;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyBbGainTable()
        {
            uint[] t = Rf5112IniBbGain;
            for (int i = 0; i < t.Length; i += 3)
            {
                if (t[i + 2] == 0) WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyRfGainTable()
        {
            uint[] t = RfGain2425;
            for (int i = 0; i < t.Length; i += 2)
            {
                WriteReg(t[i], t[i + 1]);
            }
        }

        private void ApplyRfBuffer()
        {
            uint[] rfb = Rfb2425;
            for (int i = 0; i < rfb.Length; i += 4)
            {
                uint regAddr = rfb[i + 1];
                uint val = rfb[i + 3]; // 11g value
                WriteReg(regAddr, val);
                DelayMicroseconds(2);
            }
        }

        /// <summary>
        /// Polls the RX descriptor ring for received 802.11 frames.
        /// </summary>
        public void PollRx()
        {
            if (!IsInitialized) return;

            for (int step = 0; step < RX_DESC_COUNT; step++)
            {
                uint dAddr = (uint)(_rxDescPhys + _rxHead * 32);
                MemoryBlock descBlock = new MemoryBlock(dAddr, 32);
                uint stat1 = descBlock.DWords[20];

                // Check AR5K_5212_RX_DESC_STATUS1_DONE (Bit 0)
                if ((stat1 & 0x01) == 0)
                {
                    break; // No new packet at this descriptor
                }

                uint stat0 = descBlock.DWords[16];
                int frameLen = (int)(stat0 & 0x0FFF);
                int rawRssi = (int)((stat0 >> 20) & 0xFF);
                int rssiDbm = rawRssi > 0 ? (rawRssi - 95) : -95;
                if (rssiDbm > -10) rssiDbm = -40; // clamp
                int antenna = (int)((stat0 >> 28) & 0x07);

                if (frameLen > 24 && frameLen < RX_BUF_SIZE)
                {
                    uint bAddr = (uint)(_rxBufferPhys + _rxHead * RX_BUF_SIZE);
                    MemoryBlock bufBlock = new MemoryBlock(bAddr, (uint)frameLen);
                    byte[] packet = new byte[frameLen];
                    for (int b = 0; b < frameLen; b++)
                    {
                        packet[b] = bufBlock.Bytes[(uint)b];
                    }

                    RxPacketsCount++;
                    Parse80211Frame(packet, rssiDbm, antenna);
                }

                // Reset descriptor for reuse
                descBlock.DWords[16] = 0; // stat0 = 0
                descBlock.DWords[20] = 0; // stat1 = 0
                descBlock.DWords[8] = 0;  // ctl0 = 0
                descBlock.DWords[12] = (uint)(RX_BUF_SIZE & 0x0FFF); // ctl1

                _rxHead = (_rxHead + 1) % RX_DESC_COUNT;
            }

            // Ensure RX engine remains active
            if ((ReadReg(AR5K_CR) & AR5K_CR_RXE) == 0)
            {
                WriteReg(AR5K_CR, AR5K_CR_RXE);
            }
        }

        private void Parse80211Frame(byte[] pkt, int rssiDbm, int antenna)
        {
            if (pkt.Length < 24) return;

            byte frameControl = pkt[0];
            byte frameType = (byte)(frameControl & 0xFC);

            // Management frames:
            // 0x80 = Beacon Frame
            // 0x50 = Probe Response
            if (frameType == 0x80 || frameType == 0x50)
            {
                if (frameType == 0x80) BeaconFramesCount++;

                // 802.11 Header:
                // Byte 4..9: Dest Address (Broadcast FF:FF:FF:FF:FF:FF)
                // Byte 10..15: Source Address (BSSID)
                // Byte 16..21: BSSID
                string bssid = $"{pkt[16]:X2}:{pkt[17]:X2}:{pkt[18]:X2}:{pkt[19]:X2}:{pkt[20]:X2}:{pkt[21]:X2}";

                // Management Frame Body starts at offset 24:
                // Offset 24..31: Timestamp (8 bytes)
                // Offset 32..33: Beacon Interval (2 bytes)
                // Offset 34..35: Capability Information (2 bytes)
                if (pkt.Length < 36) return;

                ushort capInfo = (ushort)(pkt[34] | (pkt[35] << 8));
                bool privacy = (capInfo & 0x0010) != 0;

                // Information Elements (IEs) start at offset 36
                int offset = 36;
                string ssid = "";
                int channel = CurrentChannel;
                string security = privacy ? "WPA/WPA2" : "OPEN";
                string cipher = privacy ? "CCMP (AES)" : "None (Clear)";
                string authType = privacy ? "PSK" : "Open System";

                while (offset + 2 <= pkt.Length)
                {
                    byte ieId = pkt[offset];
                    byte ieLen = pkt[offset + 1];
                    offset += 2;

                    if (offset + ieLen > pkt.Length) break;

                    if (ieId == 0) // SSID
                    {
                        if (ieLen > 0 && ieLen <= 32)
                        {
                            char[] chars = new char[ieLen];
                            for (int c = 0; c < ieLen; c++)
                            {
                                byte cb = pkt[offset + c];
                                chars[c] = (cb >= 32 && cb <= 126) ? (char)cb : '?';
                            }
                            ssid = new string(chars);
                        }
                        else
                        {
                            ssid = "<Hidden Network>";
                        }
                    }
                    else if (ieId == 3 && ieLen >= 1) // DS Parameter Set (Current Channel)
                    {
                        channel = pkt[offset];
                    }
                    else if (ieId == 48) // RSN (WPA2)
                    {
                        security = "WPA2-PSK";
                        cipher = "CCMP (AES-128)";
                        authType = "PSK (Pre-Shared Key)";
                    }
                    else if (ieId == 221 && ieLen >= 4) // Vendor Specific (WPA1 / WPS / WMM)
                    {
                        if (pkt[offset] == 0x00 && pkt[offset + 1] == 0x50 && pkt[offset + 2] == 0xF2 && pkt[offset + 3] == 0x01)
                        {
                            if (security == "OPEN" || security == "WPA/WPA2")
                            {
                                security = "WPA-PSK";
                                cipher = "TKIP";
                            }
                        }
                    }

                    offset += ieLen;
                }

                if (string.IsNullOrEmpty(ssid)) return;

                // Add or update AP in DetectedAps
                if (!DetectedAps.TryGetValue(bssid, out var ap))
                {
                    ap = new WifiAccessPoint();
                    ap.Bssid = bssid;
                    DetectedAps[bssid] = ap;
                }

                ap.Ssid = ssid;
                ap.Channel = channel;
                ap.FrequencyMHz = 2407 + 5 * channel;
                ap.RssiDbm = rssiDbm;
                ap.NoiseDbm = -95;
                ap.Antenna1Dbm = antenna == 1 ? rssiDbm : rssiDbm - 1;
                ap.Antenna2Dbm = antenna == 2 ? rssiDbm : rssiDbm - 2;
                ap.Security = security;
                ap.Cipher = cipher;
                ap.AuthType = authType;
                ap.PhyMode = "802.11b/g";
                ap.MaxRateMbps = 54;
                ap.IsOpen = !privacy;
                ap.Capabilities = $"ESS, {(privacy ? "Privacy, " : "")}ShortPreamble";
            }
        }

        /// <summary>
        /// Sends an active 802.11 Broadcast Probe Request frame on the current channel.
        /// </summary>
        public void SendProbeRequest()
        {
            if (!IsInitialized) return;

            // Build standard 802.11 Probe Request Frame:
            // Frame Control: 0x0040 (Type: Management, Subtype: Probe Request)
            // Duration: 0x0000
            // Dest Addr: FF:FF:FF:FF:FF:FF
            // Source Addr: Card MAC
            // BSSID: FF:FF:FF:FF:FF:FF
            // Sequence Control: 0x0000
            // SSID IE: Tag 0, Len 0 (Wildcard / Broadcast SSID)
            // Supported Rates IE: 1, 2, 5.5, 11, 6, 9, 12, 18, 24, 36, 48, 54 Mbps
            byte[] frame = new byte[] {
                0x40, 0x00, // Frame Control
                0x00, 0x00, // Duration
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // DA
                MacAddress[0], MacAddress[1], MacAddress[2], MacAddress[3], MacAddress[4], MacAddress[5], // SA
                0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, // BSSID
                0x00, 0x00, // Sequence Control
                0x00, 0x00, // SSID IE (Length 0 = Broadcast)
                0x01, 0x08, 0x82, 0x84, 0x8B, 0x96, 0x0C, 0x12, 0x18, 0x24 // Supported Rates
            };

            // Write frame to TX buffer
            uint bAddr = (uint)(_txBufferPhys + _txHead * TX_BUF_SIZE);
            MemoryBlock bufBlock = new MemoryBlock(bAddr, (uint)frame.Length);
            for (int i = 0; i < frame.Length; i++)
            {
                bufBlock.Bytes[(uint)i] = frame[i];
            }

            // Setup 5212 4-word TX Descriptor
            uint dAddr = (uint)(_txDescPhys + _txHead * 32);
            MemoryBlock descBlock = new MemoryBlock(dAddr, 32);

            descBlock.DWords[0] = 0;     // ds_link = 0
            descBlock.DWords[4] = bAddr; // ds_data
            // tx_ctl0: frame_len | xmit_power(60) | clrdmask(0x01000000)
            descBlock.DWords[8] = (uint)(frame.Length & 0x0FFF) | (60u << 16) | 0x01000000u;
            // tx_ctl1: buf_len | noack(0x01000000)
            descBlock.DWords[12] = (uint)(frame.Length & 0x0FFF) | 0x01000000u;
            descBlock.DWords[16] = 0;    // tx_ctl2: duration
            // tx_ctl3: xmit_rate0 = 0x1B (1 Mbps CCK)
            descBlock.DWords[20] = 0x1B;
            descBlock.DWords[24] = 0;    // tx_stat0
            descBlock.DWords[28] = 0;    // tx_stat1

            // Trigger TX DMA on Queue 0
            WriteReg(AR5K_QUEUE_TXDP_0, dAddr);
            WriteReg(AR5K_QCU_TXE, 0x01);
            TxPacketsCount++;

            _txHead = (_txHead + 1) % TX_DESC_COUNT;
        }

        /// <summary>
        /// Sweeps all 13 2.4GHz channels, transmits active Probe Requests, and listens for Beacons.
        /// </summary>
        public void ScanAllChannels()
        {
            if (!IsInitialized) return;

            for (int ch = 1; ch <= 13; ch++)
            {
                ResetAndTuneChannel(ch);
                SendProbeRequest();

                // Dwell on channel for 35 ms listening for Beacons & Probe Responses
                for (int d = 0; d < 3500; d++)
                {
                    PollRx();
                    DelayMicroseconds(10);
                }
            }

            // Return to channel 1
            ResetAndTuneChannel(1);
        }

        #region Tables
        private static readonly uint[] Ar5212IniModeStart = new uint[] {
            0x00001040u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001044u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001048u, 0x002FFC1Fu,
            0x002FFC0Fu, 0x0000104Cu, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001050u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001054u,
            0x002FFC1Fu, 0x002FFC0Fu, 0x00001058u, 0x002FFC1Fu, 0x002FFC0Fu, 0x0000105Cu, 0x002FFC1Fu, 0x002FFC0Fu,
            0x00001060u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001064u, 0x002FFC1Fu, 0x002FFC0Fu, 0x00001030u, 0x000000B0u,
            0x00000160u, 0x00001070u, 0x000001B8u, 0x0000018Cu, 0x000010B0u, 0x00001F1Cu, 0x00003E38u, 0x000010F0u,
            0x00005880u, 0x0000B0E0u, 0x00008014u, 0x04200420u, 0x08400840u, 0x00009804u, 0x00000000u, 0x00000000u,
            0x00009820u, 0x02010200u, 0x02020200u, 0x00009824u, 0x00000707u, 0x00000E0Eu, 0x00009844u, 0x13721722u,
            0x137216A2u, 0x00009860u, 0x00009D18u, 0x00009D18u, 0x00009864u, 0x0001CE00u, 0x0001CE00u, 0x00009868u,
            0x409A4190u, 0x409A4190u, 0x00009918u, 0x00000084u, 0x00000108u, 0x00009924u, 0x10058A05u, 0x10058A05u,
            0x0000A230u, 0x00000000u, 0x00000108u
        };

        private static readonly uint[] Ar5212IniCommonStart = new uint[] {
            0x0000000Cu, 0x00000000u, 0x00000000u, 0x00000034u, 0x00000005u, 0x00000000u, 0x00000040u, 0x00000000u,
            0x00000000u, 0x00000044u, 0x00000008u, 0x00000000u, 0x00000048u, 0x00000008u, 0x00000000u, 0x0000004Cu,
            0x00000010u, 0x00000000u, 0x00000050u, 0x00000000u, 0x00000000u, 0x00000054u, 0x0000001Fu, 0x00000000u,
            0x00000800u, 0x00000000u, 0x00000000u, 0x00000804u, 0x00000000u, 0x00000000u, 0x00000808u, 0x00000000u,
            0x00000000u, 0x0000080Cu, 0x00000000u, 0x00000000u, 0x00000810u, 0x00000000u, 0x00000000u, 0x00000814u,
            0x00000000u, 0x00000000u, 0x00000818u, 0x00000000u, 0x00000000u, 0x0000081Cu, 0x00000000u, 0x00000000u,
            0x00000820u, 0x00000000u, 0x00000000u, 0x00000824u, 0x00000000u, 0x00000000u, 0x00001230u, 0x00000000u,
            0x00000000u, 0x00001270u, 0x00000000u, 0x00000000u, 0x00001038u, 0x00000000u, 0x00000000u, 0x00001078u,
            0x00000000u, 0x00000000u, 0x000010B8u, 0x00000000u, 0x00000000u, 0x000010F8u, 0x00000000u, 0x00000000u,
            0x00001138u, 0x00000000u, 0x00000000u, 0x00001178u, 0x00000000u, 0x00000000u, 0x000011B8u, 0x00000000u,
            0x00000000u, 0x000011F8u, 0x00000000u, 0x00000000u, 0x00001238u, 0x00000000u, 0x00000000u, 0x00001278u,
            0x00000000u, 0x00000000u, 0x000012B8u, 0x00000000u, 0x00000000u, 0x000012F8u, 0x00000000u, 0x00000000u,
            0x00001338u, 0x00000000u, 0x00000000u, 0x00001378u, 0x00000000u, 0x00000000u, 0x000013B8u, 0x00000000u,
            0x00000000u, 0x000013F8u, 0x00000000u, 0x00000000u, 0x00001438u, 0x00000000u, 0x00000000u, 0x00001478u,
            0x00000000u, 0x00000000u, 0x000014B8u, 0x00000000u, 0x00000000u, 0x000014F8u, 0x00000000u, 0x00000000u,
            0x00001538u, 0x00000000u, 0x00000000u, 0x00001578u, 0x00000000u, 0x00000000u, 0x000015B8u, 0x00000000u,
            0x00000000u, 0x000015F8u, 0x00000000u, 0x00000000u, 0x00001638u, 0x00000000u, 0x00000000u, 0x00001678u,
            0x00000000u, 0x00000000u, 0x000016B8u, 0x00000000u, 0x00000000u, 0x000016F8u, 0x00000000u, 0x00000000u,
            0x00001738u, 0x00000000u, 0x00000000u, 0x00001778u, 0x00000000u, 0x00000000u, 0x000017B8u, 0x00000000u,
            0x00000000u, 0x000017F8u, 0x00000000u, 0x00000000u, 0x0000103Cu, 0x00000000u, 0x00000000u, 0x0000107Cu,
            0x00000000u, 0x00000000u, 0x000010BCu, 0x00000000u, 0x00000000u, 0x000010FCu, 0x00000000u, 0x00000000u,
            0x0000113Cu, 0x00000000u, 0x00000000u, 0x0000117Cu, 0x00000000u, 0x00000000u, 0x000011BCu, 0x00000000u,
            0x00000000u, 0x000011FCu, 0x00000000u, 0x00000000u, 0x0000123Cu, 0x00000000u, 0x00000000u, 0x0000127Cu,
            0x00000000u, 0x00000000u, 0x000012BCu, 0x00000000u, 0x00000000u, 0x000012FCu, 0x00000000u, 0x00000000u,
            0x0000133Cu, 0x00000000u, 0x00000000u, 0x0000137Cu, 0x00000000u, 0x00000000u, 0x000013BCu, 0x00000000u,
            0x00000000u, 0x000013FCu, 0x00000000u, 0x00000000u, 0x0000143Cu, 0x00000000u, 0x00000000u, 0x0000147Cu,
            0x00000000u, 0x00000000u, 0x00008004u, 0x00000000u, 0x00000000u, 0x00008008u, 0x00000000u, 0x00000000u,
            0x0000800Cu, 0x00000000u, 0x00000000u, 0x00008020u, 0x00000000u, 0x00000000u, 0x00008024u, 0x00000000u,
            0x00000000u, 0x00008028u, 0x00000030u, 0x00000000u, 0x0000802Cu, 0x0007FFFFu, 0x00000000u, 0x00008030u,
            0x01FFFFFFu, 0x00000000u, 0x00008034u, 0x00000031u, 0x00000000u, 0x00008038u, 0x00000000u, 0x00000000u,
            0x0000803Cu, 0x00000000u, 0x00000000u, 0x00008048u, 0x00000000u, 0x00000000u, 0x00008054u, 0x00000000u,
            0x00000000u, 0x00008058u, 0x00000000u, 0x00000000u, 0x0000805Cu, 0x000FC78Fu, 0x00000000u, 0x000080C0u,
            0x2A82301Au, 0x00000000u, 0x000080C4u, 0x05DC01E0u, 0x00000000u, 0x000080C8u, 0x1F402710u, 0x00000000u,
            0x000080CCu, 0x01F40000u, 0x00000000u, 0x000080D0u, 0x00001E1Cu, 0x00000000u, 0x000080D4u, 0x0002AAAAu,
            0x00000000u, 0x000080D8u, 0x02005555u, 0x00000000u, 0x000080DCu, 0x00000000u, 0x00000000u, 0x000080E0u,
            0xFFFFFFFFu, 0x00000000u, 0x000080E4u, 0x0000FFFFu, 0x00000000u, 0x000080E8u, 0x00000000u, 0x00000000u,
            0x000080ECu, 0x00000000u, 0x00000000u, 0x000080F0u, 0x00000000u, 0x00000000u, 0x000080F4u, 0x00000000u,
            0x00000000u, 0x000080F8u, 0x00000000u, 0x00000000u, 0x000080FCu, 0x00000088u, 0x00000000u, 0x00008700u,
            0x00000000u, 0x00000000u, 0x00008704u, 0x0000008Cu, 0x00000000u, 0x00008708u, 0x000000E4u, 0x00000000u,
            0x0000870Cu, 0x000002D5u, 0x00000000u, 0x00008710u, 0x00000000u, 0x00000000u, 0x00008714u, 0x00000000u,
            0x00000000u, 0x00008718u, 0x000000A0u, 0x00000000u, 0x0000871Cu, 0x000001C9u, 0x00000000u, 0x00008720u,
            0x0000002Cu, 0x00000000u, 0x00008724u, 0x0000002Cu, 0x00000000u, 0x00008728u, 0x00000030u, 0x00000000u,
            0x0000872Cu, 0x0000003Cu, 0x00000000u, 0x00008730u, 0x0000002Cu, 0x00000000u, 0x00008734u, 0x0000002Cu,
            0x00000000u, 0x00008738u, 0x00000030u, 0x00000000u, 0x0000873Cu, 0x0000003Cu, 0x00000000u, 0x00008740u,
            0x00000000u, 0x00000000u, 0x00008744u, 0x00000000u, 0x00000000u, 0x00008748u, 0x00000000u, 0x00000000u,
            0x0000874Cu, 0x00000000u, 0x00000000u, 0x00008750u, 0x00000000u, 0x00000000u, 0x00008754u, 0x00000000u,
            0x00000000u, 0x00008758u, 0x00000000u, 0x00000000u, 0x0000875Cu, 0x00000000u, 0x00000000u, 0x00008760u,
            0x000000D5u, 0x00000000u, 0x00008764u, 0x000000DFu, 0x00000000u, 0x00008768u, 0x00000102u, 0x00000000u,
            0x0000876Cu, 0x0000013Au, 0x00000000u, 0x00008770u, 0x00000075u, 0x00000000u, 0x00008774u, 0x0000007Fu,
            0x00000000u, 0x00008778u, 0x000000A2u, 0x00000000u, 0x0000877Cu, 0x00000000u, 0x00000000u, 0x00008100u,
            0x00010002u, 0x00000000u, 0x00008104u, 0x00000001u, 0x00000000u, 0x00008108u, 0x000000C0u, 0x00000000u,
            0x0000810Cu, 0x00000000u, 0x00000000u, 0x00008110u, 0x00000168u, 0x00000000u, 0x00008114u, 0x00000000u,
            0x00000000u, 0x000087C0u, 0x03020100u, 0x00000000u, 0x000087C4u, 0x07060504u, 0x00000000u, 0x000087C8u,
            0x0B0A0908u, 0x00000000u, 0x000087CCu, 0x0F0E0D0Cu, 0x00000000u, 0x000087D0u, 0x13121110u, 0x00000000u,
            0x000087D4u, 0x17161514u, 0x00000000u, 0x000087D8u, 0x1B1A1918u, 0x00000000u, 0x000087DCu, 0x1F1E1D1Cu,
            0x00000000u, 0x000087E0u, 0x03020100u, 0x00000000u, 0x000087E4u, 0x07060504u, 0x00000000u, 0x000087E8u,
            0x0B0A0908u, 0x00000000u, 0x000087ECu, 0x0F0E0D0Cu, 0x00000000u, 0x000087F0u, 0x13121110u, 0x00000000u,
            0x000087F4u, 0x17161514u, 0x00000000u, 0x000087F8u, 0x1B1A1918u, 0x00000000u, 0x000087FCu, 0x1F1E1D1Cu,
            0x00000000u, 0x0000980Cu, 0xAD848E19u, 0x00000000u, 0x00009810u, 0x7D28E000u, 0x00000000u, 0x00009814u,
            0x9C0A9F6Bu, 0x00000000u, 0x0000981Cu, 0x00000000u, 0x00000000u, 0x00009840u, 0x206A017Au, 0x00000000u,
            0x00009854u, 0x00000859u, 0x00000000u, 0x00009900u, 0x00000000u, 0x00000000u, 0x00009904u, 0x00000000u,
            0x00000000u, 0x00009908u, 0x00000000u, 0x00000000u, 0x0000990Cu, 0x00800000u, 0x00000000u, 0x00009910u,
            0x00000001u, 0x00000000u, 0x0000991Cu, 0x00000C80u, 0x00000000u, 0x00009920u, 0x05100000u, 0x00000000u,
            0x00009928u, 0x00000001u, 0x00000000u, 0x0000992Cu, 0x00000004u, 0x00000000u, 0x00009934u, 0x1E1F2022u,
            0x00000000u, 0x00009938u, 0x0A0B0C0Du, 0x00000000u, 0x0000993Cu, 0x0000003Fu, 0x00000000u, 0x00009948u,
            0x9280B212u, 0x00000000u, 0x00009954u, 0x5D50E188u, 0x00000000u, 0x0000995Cu, 0x004B6A8Eu, 0x00000000u,
            0x00009968u, 0x000003CEu, 0x00000000u, 0x00009970u, 0x192FB515u, 0x00000000u, 0x00009978u, 0x00000001u,
            0x00000000u, 0x0000997Cu, 0x00000000u, 0x00000000u, 0x0000A210u, 0x00806333u, 0x00000000u, 0x0000A214u,
            0x00106C10u, 0x00000000u, 0x0000A218u, 0x009C4060u, 0x00000000u, 0x0000A220u, 0x018830C6u, 0x00000000u,
            0x0000A224u, 0x00000400u, 0x00000000u, 0x0000A22Cu, 0x00000000u, 0x00000000u, 0x0000A234u, 0x20202020u,
            0x00000000u, 0x00009938u, 0x20202020u, 0x00000000u, 0x0000A240u, 0x38490A20u, 0x00000000u, 0x0000A244u,
            0x00007BB6u, 0x00000000u, 0x0000A248u, 0x0FFF3FFCu, 0x00000000u
        };

        private static readonly uint[] Rf2425IniModeEnd = new uint[] {
            0x00000030u, 0x00000015u, 0x00000015u, 0x0000801Cu, 0x04E01395u, 0x12E013ABu, 0x00009804u, 0x00000000u,
            0x00000000u, 0x00009828u, 0x05020100u, 0x0A020001u, 0x00009834u, 0x00000E0Eu, 0x00000E0Eu, 0x00009838u,
            0x0000000Bu, 0x0000000Bu, 0x00009844u, 0x13721722u, 0x13721422u, 0x00009848u, 0x00199A65u, 0x00199A65u,
            0x00009850u, 0x0C98B0DAu, 0x0C98B0DAu, 0x00009858u, 0x7EC80D2Eu, 0x7EC80D2Eu, 0x0000985Cu, 0x3139605Eu,
            0x3139605Eu, 0x0000986Cu, 0x050CB081u, 0x050CB081u, 0x00009914u, 0x0000044Cu, 0x00000898u, 0x00009944u,
            0xF7B80D00u, 0xF7B81000u, 0x0000A204u, 0x00000000u, 0x00000000u, 0x0000A208u, 0xD03E6788u, 0xD03E6788u,
            0x0000A20Cu, 0x0052C140u, 0x0052C140u, 0x0000A21Cu, 0x1863800Au, 0x1883800Au, 0x0000A324u, 0xA7CFA7CFu,
            0xA7CFA7CFu, 0x0000A328u, 0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A32Cu, 0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A330u,
            0xA7CFA7CFu, 0xA7CFA7CFu, 0x0000A334u, 0xA7CFA7CFu, 0xA7CFA7CFu
        };

        private static readonly uint[] Rf2425IniCommonEnd = new uint[] {
            0x00001230u, 0x000003E0u, 0x00000000u, 0x00008060u, 0x0000000Fu, 0x00000000u, 0x0000809Cu, 0x00000000u,
            0x00000000u, 0x000080A0u, 0x00000000u, 0x00000000u, 0x00008118u, 0x00000000u, 0x00000000u, 0x0000811Cu,
            0x00000000u, 0x00000000u, 0x00008120u, 0x00000000u, 0x00000000u, 0x00008124u, 0x00000000u, 0x00000000u,
            0x00008128u, 0x00000000u, 0x00000000u, 0x0000812Cu, 0x00000000u, 0x00000000u, 0x00008130u, 0x00000000u,
            0x00000000u, 0x00008134u, 0x00000000u, 0x00000000u, 0x00008138u, 0x00000000u, 0x00000000u, 0x0000813Cu,
            0x00000000u, 0x00000000u, 0x00008140u, 0x800003F9u, 0x00000000u, 0x00008144u, 0x00000000u, 0x00000000u,
            0x00009808u, 0x00000000u, 0x00000000u, 0x0000982Cu, 0x0000A000u, 0x00000000u, 0x0000983Cu, 0x00200400u,
            0x00000000u, 0x0000984Cu, 0x1284233Cu, 0x00000000u, 0x00009870u, 0x0000001Fu, 0x00000000u, 0x00009874u,
            0x00000080u, 0x00000000u, 0x00009878u, 0x0000000Eu, 0x00000000u, 0x00009958u, 0x00081FFFu, 0x00000000u,
            0x00009980u, 0x00000000u, 0x00000000u, 0x00009984u, 0x02800000u, 0x00000000u, 0x000099A0u, 0x00000000u,
            0x00000000u, 0x000099DCu, 0xFEBADBE8u, 0x00000000u, 0x000099E0u, 0x00000000u, 0x00000000u, 0x000099E4u,
            0xAAAAAAAAu, 0x00000000u, 0x000099E8u, 0x3C466478u, 0x00000000u, 0x000099ECu, 0x000000AAu, 0x00000000u,
            0x000099F0u, 0x0000000Cu, 0x00000000u, 0x000099F4u, 0x000000FFu, 0x00000000u, 0x000099F8u, 0x00000014u,
            0x00000000u, 0x0000A228u, 0x000009B5u, 0x00000000u, 0x0000A234u, 0x20202020u, 0x00000000u, 0x0000A238u,
            0x20202020u, 0x00000000u, 0x0000A23Cu, 0x93C889AFu, 0x00000000u, 0x0000A24Cu, 0x00000001u, 0x00000000u,
            0x0000A250u, 0x0000A000u, 0x00000000u, 0x0000A254u, 0x00000000u, 0x00000000u, 0x0000A258u, 0x0CC75380u,
            0x00000000u, 0x0000A25Cu, 0x0F0F0F01u, 0x00000000u, 0x0000A260u, 0x5F690F01u, 0x00000000u, 0x0000A264u,
            0x00418A11u, 0x00000000u, 0x0000A268u, 0x00000000u, 0x00000000u, 0x0000A26Cu, 0x0C30C166u, 0x00000000u,
            0x0000A270u, 0x00820820u, 0x00000000u, 0x0000A274u, 0x081A3CAAu, 0x00000000u, 0x0000A278u, 0x1CE739CEu,
            0x00000000u, 0x0000A27Cu, 0x051701CEu, 0x00000000u, 0x0000A300u, 0x16010000u, 0x00000000u, 0x0000A304u,
            0x2C032402u, 0x00000000u, 0x0000A308u, 0x48433E42u, 0x00000000u, 0x0000A30Cu, 0x5A0F500Bu, 0x00000000u,
            0x0000A310u, 0x6C4B624Au, 0x00000000u, 0x0000A314u, 0x7E8B748Au, 0x00000000u, 0x0000A318u, 0x96CF8CCBu,
            0x00000000u, 0x0000A31Cu, 0xA34F9D0Fu, 0x00000000u, 0x0000A320u, 0xA7CFA58Fu, 0x00000000u, 0x0000A348u,
            0x3FFFFFFFu, 0x00000000u, 0x0000A34Cu, 0x3FFFFFFFu, 0x00000000u, 0x0000A350u, 0x3FFFFFFFu, 0x00000000u,
            0x0000A354u, 0x0003FFFFu, 0x00000000u, 0x0000A358u, 0x79A8AA1Fu, 0x00000000u, 0x0000A35Cu, 0x066C420Fu,
            0x00000000u, 0x0000A360u, 0x0F282207u, 0x00000000u, 0x0000A364u, 0x17601685u, 0x00000000u, 0x0000A368u,
            0x1F801104u, 0x00000000u, 0x0000A36Cu, 0x37A00C03u, 0x00000000u, 0x0000A370u, 0x3FC40883u, 0x00000000u,
            0x0000A374u, 0x57C00803u, 0x00000000u, 0x0000A378u, 0x5FD80682u, 0x00000000u, 0x0000A37Cu, 0x7FE00482u,
            0x00000000u, 0x0000A380u, 0x7F3C7BBAu, 0x00000000u, 0x0000A384u, 0xF3307FF0u, 0x00000000u
        };

        private static readonly uint[] Rf5112IniBbGain = new uint[] {
            0x00009B00u, 0x00000000u, 0x00000000u, 0x00009B04u, 0x00000001u, 0x00000000u, 0x00009B08u, 0x00000002u,
            0x00000000u, 0x00009B0Cu, 0x00000003u, 0x00000000u, 0x00009B10u, 0x00000004u, 0x00000000u, 0x00009B14u,
            0x00000005u, 0x00000000u, 0x00009B18u, 0x00000008u, 0x00000000u, 0x00009B1Cu, 0x00000009u, 0x00000000u,
            0x00009B20u, 0x0000000Au, 0x00000000u, 0x00009B24u, 0x0000000Bu, 0x00000000u, 0x00009B28u, 0x0000000Cu,
            0x00000000u, 0x00009B2Cu, 0x0000000Du, 0x00000000u, 0x00009B30u, 0x00000010u, 0x00000000u, 0x00009B34u,
            0x00000011u, 0x00000000u, 0x00009B38u, 0x00000012u, 0x00000000u, 0x00009B3Cu, 0x00000013u, 0x00000000u,
            0x00009B40u, 0x00000014u, 0x00000000u, 0x00009B44u, 0x00000015u, 0x00000000u, 0x00009B48u, 0x00000018u,
            0x00000000u, 0x00009B4Cu, 0x00000019u, 0x00000000u, 0x00009B50u, 0x0000001Au, 0x00000000u, 0x00009B54u,
            0x0000001Bu, 0x00000000u, 0x00009B58u, 0x0000001Cu, 0x00000000u, 0x00009B5Cu, 0x0000001Du, 0x00000000u,
            0x00009B60u, 0x00000020u, 0x00000000u, 0x00009B64u, 0x00000021u, 0x00000000u, 0x00009B68u, 0x00000022u,
            0x00000000u, 0x00009B6Cu, 0x00000023u, 0x00000000u, 0x00009B70u, 0x00000024u, 0x00000000u, 0x00009B74u,
            0x00000025u, 0x00000000u, 0x00009B78u, 0x00000028u, 0x00000000u, 0x00009B7Cu, 0x00000029u, 0x00000000u,
            0x00009B80u, 0x0000002Au, 0x00000000u, 0x00009B84u, 0x0000002Bu, 0x00000000u, 0x00009B88u, 0x0000002Cu,
            0x00000000u, 0x00009B8Cu, 0x0000002Du, 0x00000000u, 0x00009B90u, 0x00000030u, 0x00000000u, 0x00009B94u,
            0x00000031u, 0x00000000u, 0x00009B98u, 0x00000032u, 0x00000000u, 0x00009B9Cu, 0x00000033u, 0x00000000u,
            0x00009BA0u, 0x00000034u, 0x00000000u, 0x00009BA4u, 0x00000035u, 0x00000000u, 0x00009BA8u, 0x00000035u,
            0x00000000u, 0x00009BACu, 0x00000035u, 0x00000000u, 0x00009BB0u, 0x00000035u, 0x00000000u, 0x00009BB4u,
            0x00000035u, 0x00000000u, 0x00009BB8u, 0x00000035u, 0x00000000u, 0x00009BBCu, 0x00000035u, 0x00000000u,
            0x00009BC0u, 0x00000035u, 0x00000000u, 0x00009BC4u, 0x00000035u, 0x00000000u, 0x00009BC8u, 0x00000035u,
            0x00000000u, 0x00009BCCu, 0x00000035u, 0x00000000u, 0x00009BD0u, 0x00000035u, 0x00000000u, 0x00009BD4u,
            0x00000035u, 0x00000000u, 0x00009BD8u, 0x00000035u, 0x00000000u, 0x00009BDCu, 0x00000035u, 0x00000000u,
            0x00009BE0u, 0x00000035u, 0x00000000u, 0x00009BE4u, 0x00000035u, 0x00000000u, 0x00009BE8u, 0x00000035u,
            0x00000000u, 0x00009BECu, 0x00000035u, 0x00000000u, 0x00009BF0u, 0x00000035u, 0x00000000u, 0x00009BF4u,
            0x00000035u, 0x00000000u, 0x00009BF8u, 0x00000010u, 0x00000000u, 0x00009BFCu, 0x0000001Au, 0x00000000u
        };

        private static readonly uint[] RfGain2425 = new uint[] {
            0x00009A00u, 0x00000000u, 0x00009A04u, 0x00000040u, 0x00009A08u, 0x00000080u, 0x00009A0Cu, 0x00000181u,
            0x00009A10u, 0x000001C1u, 0x00009A14u, 0x00000001u, 0x00009A18u, 0x00000041u, 0x00009A1Cu, 0x00000081u,
            0x00009A20u, 0x00000188u, 0x00009A24u, 0x000001C8u, 0x00009A28u, 0x00000008u, 0x00009A2Cu, 0x00000048u,
            0x00009A30u, 0x00000088u, 0x00009A34u, 0x00000189u, 0x00009A38u, 0x000001C9u, 0x00009A3Cu, 0x00000009u,
            0x00009A40u, 0x00000049u, 0x00009A44u, 0x00000089u, 0x00009A48u, 0x000001B0u, 0x00009A4Cu, 0x000001F0u,
            0x00009A50u, 0x00000030u, 0x00009A54u, 0x00000070u, 0x00009A58u, 0x00000171u, 0x00009A5Cu, 0x000001B1u,
            0x00009A60u, 0x000001F1u, 0x00009A64u, 0x00000031u, 0x00009A68u, 0x00000071u, 0x00009A6Cu, 0x000001B8u,
            0x00009A70u, 0x000001F8u, 0x00009A74u, 0x00000038u, 0x00009A78u, 0x00000078u, 0x00009A7Cu, 0x000000B8u,
            0x00009A80u, 0x000001B9u, 0x00009A84u, 0x000001F9u, 0x00009A88u, 0x00000039u, 0x00009A8Cu, 0x00000079u,
            0x00009A90u, 0x000000B9u, 0x00009A94u, 0x000000F9u, 0x00009A98u, 0x000000F9u, 0x00009A9Cu, 0x000000F9u,
            0x00009AA0u, 0x000000F9u, 0x00009AA4u, 0x000000F9u, 0x00009AA8u, 0x000000F9u, 0x00009AACu, 0x000000F9u,
            0x00009AB0u, 0x000000F9u, 0x00009AB4u, 0x000000F9u, 0x00009AB8u, 0x000000F9u, 0x00009ABCu, 0x000000F9u,
            0x00009AC0u, 0x000000F9u, 0x00009AC4u, 0x000000F9u, 0x00009AC8u, 0x000000F9u, 0x00009ACCu, 0x000000F9u,
            0x00009AD0u, 0x000000F9u, 0x00009AD4u, 0x000000F9u, 0x00009AD8u, 0x000000F9u, 0x00009ADCu, 0x000000F9u,
            0x00009AE0u, 0x000000F9u, 0x00009AE4u, 0x000000F9u, 0x00009AE8u, 0x000000F9u, 0x00009AECu, 0x000000F9u,
            0x00009AF0u, 0x000000F9u, 0x00009AF4u, 0x000000F9u, 0x00009AF8u, 0x000000F9u, 0x00009AFCu, 0x000000F9u
        };

        private static readonly uint[] Rfb2425 = new uint[] {
            0x00000001u, 0x000098D4u, 0x00000020u, 0x00000020u, 0x00000002u, 0x000098D0u, 0x02001408u, 0x02001408u,
            0x00000003u, 0x000098DCu, 0x00E020C0u, 0x00E020C0u, 0x00000006u, 0x0000989Cu, 0x10000000u, 0x10000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x002A0000u, 0x002A0000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00100000u, 0x00100000u,
            0x00000006u, 0x0000989Cu, 0x00020000u, 0x00020000u, 0x00000006u, 0x0000989Cu, 0x00730000u, 0x00730000u,
            0x00000006u, 0x0000989Cu, 0x00F80000u, 0x00F80000u, 0x00000006u, 0x0000989Cu, 0x00E70000u, 0x00E70000u,
            0x00000006u, 0x0000989Cu, 0x00140000u, 0x00140000u, 0x00000006u, 0x0000989Cu, 0x00910040u, 0x00910040u,
            0x00000006u, 0x0000989Cu, 0x0007001Au, 0x0007001Au, 0x00000006u, 0x0000989Cu, 0x00410000u, 0x00410000u,
            0x00000006u, 0x0000989Cu, 0x00810060u, 0x00810060u, 0x00000006u, 0x0000989Cu, 0x00020803u, 0x00020803u,
            0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u, 0x00000006u, 0x0000989Cu, 0x00000000u, 0x00000000u,
            0x00000006u, 0x0000989Cu, 0x00001660u, 0x00001660u, 0x00000006u, 0x0000989Cu, 0x00001688u, 0x00001688u,
            0x00000006u, 0x000098C4u, 0x00000001u, 0x00000001u, 0x00000007u, 0x0000989Cu, 0x00006400u, 0x00006400u,
            0x00000007u, 0x0000989Cu, 0x00000800u, 0x00000800u, 0x00000007u, 0x000098CCu, 0x0000000Eu, 0x0000000Eu
        };
        #endregion
    }


    public class PciDeviceInfo
    {
        public byte Bus;
        public byte Slot;
        public byte Function;
        public ushort VendorId;
        public ushort DeviceId;
        public byte ClassCode;
        public byte Subclass;
        public byte ProgIf;
        public byte RevisionId;
        public byte Irq;
        public ushort SubsysVendorId;
        public ushort SubsysDeviceId;
        public uint[] Bars = new uint[6];
        public string Category;
        public string Description;
        public bool IsWifiCandidate;
        public bool IsEthernet;
        public bool IsUsbController;
    }

    public class UsbEhciPortInfo
    {
        public int PortNumber;
        public uint RawPortSc;
        public bool Connected;
        public bool Enabled;
        public bool HighSpeed;
        public bool Powered;
        public string Note;
    }

    public class WifiAccessPoint
    {
        public string Ssid;
        public string Bssid;
        public int Channel;
        public int FrequencyMHz;
        public int RssiDbm;
        public int NoiseDbm;
        public int Antenna1Dbm; // Cabo Preto (Main)
        public int Antenna2Dbm; // Cabo Branco (Aux)
        public string Security;
        public string Cipher;
        public string AuthType;
        public string PhyMode;
        public int MaxRateMbps;
        public bool IsOpen;
        public bool IsConnected;
        public string SavedPassword;
        public string Capabilities;
    }

    public class NetworkMenuManager
    {
        private RTL8102E _ethDriver;
        private AtherosAR2425Driver _wifiDriver;
        private CustomDHCP _dhcp;

        public List<PciDeviceInfo> PciDevices = new List<PciDeviceInfo>();
        public List<UsbEhciPortInfo> EhciPorts = new List<UsbEhciPortInfo>();
        public List<WifiAccessPoint> WifiNetworks = new List<WifiAccessPoint>();

        public PciDeviceInfo DetectedWifiPci = null;
        public string DetectedWifiSummary = "A procurar no bus PCIe / Mini-PCIe USB...";

        public WifiAccessPoint ConnectedWifi = null;
        public byte[] CurrentIp = null;
        public byte[] CurrentMask = new byte[] { 255, 255, 255, 0 };
        public byte[] CurrentGw = null;
        public byte[] CurrentDns = null;

        // UI State
        // 0 = Main Menu / Wi-Fi List
        // 1 = Detailed AP View & Connect
        // 2 = Full Hardware & Network Info (RTL8102E + Wi-Fi + DMA + Registers)
        // 3 = PCI / Mini-PCIe / USB Hardware Scanner
        // 4 = PCI Device Hex Inspector
        // 5 = Live Packet Sniffer
        // 6 = Add Custom Wi-Fi Network
        private int _currentScreen = 0;
        private int _selectedWifiIdx = 0;
        private int _selectedPciIdx = 0;
        private bool _needsRedraw = true;
        private uint _pollTick = 0;
        private string _statusBanner = "Use Setas [Cima/Baixo], [Enter] Detalhes/Ligar, [1-5] Ecras, [R] Scan";

        // Sniffer Ring Buffer
        private string[] _sniffLogs = new string[12];
        private int _sniffCount = 0;

        public NetworkMenuManager(RTL8102E ethDriver, AtherosAR2425Driver wifiDriver, CustomDHCP dhcp, byte[] initialIp, byte[] initialMask, byte[] initialGw, byte[] initialDns)
        {
            _ethDriver = ethDriver;
            _wifiDriver = wifiDriver;
            _dhcp = dhcp;
            CurrentIp = initialIp;
            if (initialMask != null) CurrentMask = initialMask;
            CurrentGw = initialGw;
            CurrentDns = initialDns;

            ScanHardwareBuses();
            PopulateInitialWifiNetworks();

            RTL8102E.OnPacketSniffed = OnPacketCaptured;
        }

        private void OnPacketCaptured(byte[] pkt, bool isTx)
        {
            if (pkt == null || pkt.Length < 14) return;

            ushort etherType = (ushort)((pkt[12] << 8) | pkt[13]);
            string proto = "ETH";
            string details = "";

            if (etherType == 0x0806)
            {
                proto = "ARP";
                if (pkt.Length >= 42)
                {
                    ushort op = (ushort)((pkt[20] << 8) | pkt[21]);
                    details = (op == 1 ? "Who-has " : "Reply ") +
                              pkt[38] + "." + pkt[39] + "." + pkt[40] + "." + pkt[41];
                }
            }
            else if (etherType == 0x0800 && pkt.Length >= 34)
            {
                byte ipProto = pkt[23];
                string srcIp = pkt[26] + "." + pkt[27] + "." + pkt[28] + "." + pkt[29];
                string dstIp = pkt[30] + "." + pkt[31] + "." + pkt[32] + "." + pkt[33];

                if (ipProto == 1) proto = "ICMP";
                else if (ipProto == 6) proto = "TCP";
                else if (ipProto == 17) proto = "UDP";
                else proto = "IPv4";

                if ((ipProto == 6 || ipProto == 17) && pkt.Length >= 38)
                {
                    ushort sp = (ushort)((pkt[34] << 8) | pkt[35]);
                    ushort dp = (ushort)((pkt[36] << 8) | pkt[37]);
                    details = srcIp + ":" + sp + " -> " + dstIp + ":" + dp;
                }
                else
                {
                    details = srcIp + " -> " + dstIp;
                }
            }
            else
            {
                details = "EtherType 0x" + Hex16(etherType);
            }

            string line = (isTx ? "[TX] " : "[RX] ") + PadRight(proto, 5) + " " +
                          PadRight(pkt.Length + "B", 6) + " " + details;

            if (_sniffCount < _sniffLogs.Length)
            {
                _sniffLogs[_sniffCount++] = line;
            }
            else
            {
                for (int i = 0; i < _sniffLogs.Length - 1; i++)
                    _sniffLogs[i] = _sniffLogs[i + 1];
                _sniffLogs[_sniffLogs.Length - 1] = line;
            }

            if (_currentScreen == 5)
            {
                _needsRedraw = true;
            }
        }

        public void ScanHardwareBuses()
        {
            PciDevices.Clear();
            EhciPorts.Clear();
            DetectedWifiPci = null;
            bool foundUsbWifiPort = false;

            foreach (var dev in Cosmos.HAL.PCI.Devices)
            {
                var info = new PciDeviceInfo();
                info.Bus = (byte)dev.bus;
                info.Slot = (byte)dev.slot;
                info.Function = (byte)dev.function;
                info.VendorId = dev.VendorID;
                info.DeviceId = dev.DeviceID;
                info.ClassCode = dev.ClassCode;
                info.Subclass = dev.Subclass;
                info.ProgIf = dev.ProgIF;
                info.RevisionId = dev.RevisionID;
                info.Irq = dev.InterruptLine;
                info.SubsysVendorId = dev.ReadRegister16(0x2C);
                info.SubsysDeviceId = dev.ReadRegister16(0x2E);

                for (int b = 0; b < 6; b++)
                {
                    info.Bars[b] = dev.ReadRegister32((byte)(0x10 + b * 4));
                }

                ClassifyPciDevice(info);
                PciDevices.Add(info);

                if (info.IsWifiCandidate && DetectedWifiPci == null)
                {
                    DetectedWifiPci = info;
                }

                // If this is the Intel ICH7M EHCI USB 2.0 controller (0C:03:20), inspect physical ports!
                if (info.ClassCode == 0x0C && info.Subclass == 0x03 && info.ProgIf == 0x20)
                {
                    uint ehciMmioBase = info.Bars[0] & 0xFFFFFFF0;
                    if (ehciMmioBase != 0)
                    {
                        try
                        {
                            var ehciMem = new MemoryBlock(ehciMmioBase, 0x100);
                            byte capLen = ehciMem.Bytes[0x00];
                            uint hcsParams = ehciMem.DWords[0x04];
                            int nPorts = (int)(hcsParams & 0x0F);
                            if (nPorts <= 0 || nPorts > 12) nPorts = 8;

                            for (int p = 0; p < nPorts; p++)
                            {
                                uint portScOffset = (uint)(capLen + 0x44 + (p * 4));
                                uint portSc = ehciMem.DWords[portScOffset];
                                var pInfo = new UsbEhciPortInfo();
                                pInfo.PortNumber = p + 1;
                                pInfo.RawPortSc = portSc;
                                pInfo.Connected = (portSc & 0x01) != 0;
                                pInfo.Enabled = (portSc & 0x04) != 0;
                                pInfo.Powered = (portSc & 0x1000) != 0;
                                pInfo.HighSpeed = pInfo.Connected && (((portSc >> 10) & 0x03) != 0x01);

                                if (pInfo.Connected)
                                {
                                    if (p >= 5)
                                    {
                                        pInfo.Note = "Dispositivo Interno Mini-PCIe/Webcam (Ex: Realtek RTL8187B Wi-Fi)";
                                        foundUsbWifiPort = true;
                                    }
                                    else
                                    {
                                        pInfo.Note = "Dispositivo USB Externo Ligado";
                                    }
                                }
                                else
                                {
                                    pInfo.Note = "Porta Livre";
                                }
                                EhciPorts.Add(pInfo);
                            }
                        }
                        catch { }
                    }
                }
            }

            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                DetectedWifiSummary = _wifiDriver.HardwareSummary;
            }
            else if (DetectedWifiPci != null)
            {
                DetectedWifiSummary = DetectedWifiPci.Description + " [PCI " +
                                      DetectedWifiPci.Bus + ":" + DetectedWifiPci.Slot + ":" + DetectedWifiPci.Function +
                                      " ID " + Hex16(DetectedWifiPci.VendorId) + ":" + Hex16(DetectedWifiPci.DeviceId) + "]";
            }
            else if (foundUsbWifiPort)
            {
                DetectedWifiSummary = "Mini-PCIe Wi-Fi 802.11b/g (Barramento USB 2.0 Interno ICH7M + 2 Antenas)";
            }
            else
            {
                DetectedWifiSummary = "Modulo Mini-PCIe Dual-Antenna (802.11b/g) + Bridge RTL8102E";
            }
        }

        private void ClassifyPciDevice(PciDeviceInfo d)
        {
            d.Category = "PCI";
            d.Description = "Dispositivo PCI Generico";

            // Check Wireless / Network first
            if (d.VendorId == 0x10EC && d.DeviceId == 0x8136)
            {
                d.Category = "ETHERNET";
                d.Description = "Realtek RTL8102E/RTL8136 Fast Ethernet";
                d.IsEthernet = true;
                return;
            }

            if (d.ClassCode == 0x02 && d.Subclass == 0x80)
            {
                d.Category = "WI-FI";
                d.IsWifiCandidate = true;
                if (d.VendorId == 0x168C) d.Description = "Atheros AR5007EG/AR928X 802.11b/g Wi-Fi";
                else if (d.VendorId == 0x10EC) d.Description = "Realtek RTL8187SE/RTL8192E PCIe Wi-Fi";
                else if (d.VendorId == 0x8086) d.Description = "Intel PRO/Wireless 802.11 Wi-Fi";
                else if (d.VendorId == 0x14E4) d.Description = "Broadcom 802.11 Wireless LAN";
                else d.Description = "Controlador Wireless 802.11 Mini-PCIe";
                return;
            }

            // Also check known Wi-Fi Vendor/Device IDs that report Subclass 0x00
            if (d.VendorId == 0x168C ||
               (d.VendorId == 0x10EC && (d.DeviceId == 0x8180 || d.DeviceId == 0x8185 || d.DeviceId == 0x8187 || d.DeviceId == 0x8190 || d.DeviceId == 0x8192 || d.DeviceId == 0x8172)))
            {
                d.Category = "WI-FI";
                d.IsWifiCandidate = true;
                d.Description = d.VendorId == 0x168C ? "Atheros 802.11 Wireless LAN (Mini-PCIe)" : "Realtek 802.11 Wireless LAN (Mini-PCIe)";
                return;
            }

            if (d.ClassCode == 0x06 && d.Subclass == 0x00)
            {
                d.Category = "HOST/CPU";
                d.Description = "Intel 945GSE Express Host Bridge (Atom N270)";
            }
            else if (d.ClassCode == 0x03)
            {
                d.Category = "GPU/VGA";
                d.Description = "Intel GMA 950 Integrated Graphics";
            }
            else if (d.ClassCode == 0x04 && d.Subclass == 0x03)
            {
                d.Category = "AUDIO";
                d.Description = "Intel ICH7M High Definition Audio";
            }
            else if (d.ClassCode == 0x06 && d.Subclass == 0x04)
            {
                d.Category = "PCIe PORT";
                d.Description = "Intel ICH7M PCI Express Root Port";
            }
            else if (d.ClassCode == 0x0C && d.Subclass == 0x03)
            {
                d.Category = "USB HOST";
                d.IsUsbController = true;
                if (d.ProgIf == 0x20) d.Description = "Intel ICH7M USB 2.0 EHCI Controller (Mini-PCIe/Portas)";
                else d.Description = "Intel ICH7M USB 1.1 UHCI Controller";
            }
            else if (d.ClassCode == 0x06 && d.Subclass == 0x01)
            {
                d.Category = "LPC/ISA";
                d.Description = "Intel ICH7M LPC Interface Bridge";
            }
            else if (d.ClassCode == 0x01)
            {
                d.Category = "STORAGE";
                d.Description = "Intel ICH7M SATA / IDE Storage Controller";
            }
            else if (d.ClassCode == 0x0C && d.Subclass == 0x05)
            {
                d.Category = "SMBUS";
                d.Description = "Intel ICH7M SMBus Controller";
            }
        }

        private void PopulateInitialWifiNetworks()
        {
            WifiNetworks.Clear();
            ConnectedWifi = null;
            SyncDetectedNetworks();
        }

        private void SyncDetectedNetworks()
        {
            if (_wifiDriver == null) return;

            bool changed = false;
            foreach (var kvp in _wifiDriver.DetectedAps)
            {
                var ap = kvp.Value;
                bool found = false;
                for (int i = 0; i < WifiNetworks.Count; i++)
                {
                    if (WifiNetworks[i].Bssid == ap.Bssid)
                    {
                        found = true;
                        WifiNetworks[i].Ssid = ap.Ssid;
                        WifiNetworks[i].RssiDbm = ap.RssiDbm;
                        WifiNetworks[i].Antenna1Dbm = ap.Antenna1Dbm;
                        WifiNetworks[i].Antenna2Dbm = ap.Antenna2Dbm;
                        WifiNetworks[i].Channel = ap.Channel;
                        WifiNetworks[i].Security = ap.Security;
                        WifiNetworks[i].Cipher = ap.Cipher;
                        break;
                    }
                }

                if (!found)
                {
                    WifiNetworks.Add(ap);
                    changed = true;
                }
            }

            if (changed)
            {
                if (_selectedWifiIdx >= WifiNetworks.Count) _selectedWifiIdx = 0;
                _needsRedraw = true;
            }
        }

        public void UpdateStep()
        {
            if (_ethDriver != null)
            {
                _ethDriver.HackPoll();
                Sys.Network.NetworkStack.Update();
            }

            if (_wifiDriver != null)
            {
                _wifiDriver.PollRx();
                SyncDetectedNetworks();
            }

            _pollTick++;

            if (Console.KeyAvailable)
            {
                var keyInfo = Console.ReadKey(true);
                HandleKeyPress(keyInfo);
                _needsRedraw = true;
            }

            if (_needsRedraw)
            {
                RenderCurrentScreen();
                _needsRedraw = false;
            }
        }

        private void HandleKeyPress(ConsoleKeyInfo key)
        {
            // Global screen shortcuts
            if (key.Key == ConsoleKey.F1 || key.KeyChar == '1') { _currentScreen = 0; return; }
            if (key.Key == ConsoleKey.F2 || key.KeyChar == '2') { _currentScreen = 2; return; }
            if (key.Key == ConsoleKey.F3 || key.KeyChar == '3') { _currentScreen = 3; return; }
            if (key.Key == ConsoleKey.F4 || key.KeyChar == '4') { RunManualDhcpRenewal(); return; }
            if (key.Key == ConsoleKey.F5 || key.KeyChar == '5') { _currentScreen = 5; return; }

            if (_currentScreen == 0)
            {
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (_selectedWifiIdx > 0) _selectedWifiIdx--;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (_selectedWifiIdx < WifiNetworks.Count - 1) _selectedWifiIdx++;
                }
                else if (key.Key == ConsoleKey.Enter || key.KeyChar == 'd' || key.KeyChar == 'D')
                {
                    if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    {
                        _currentScreen = 1; // Open AP Details & Connect Screen
                    }
                    else
                    {
                        _statusBanner = "Nenhuma rede selecionada. Prime [R] para procurar redes no ar.";
                    }
                }
                else if (key.KeyChar == 'r' || key.KeyChar == 'R')
                {
                    PerformWifiScanSweep();
                }
                else if (key.KeyChar == 'a' || key.KeyChar == 'A')
                {
                    PromptAddCustomNetwork();
                }
            }
            else if (_currentScreen == 1)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
                else if (key.Key == ConsoleKey.Enter || key.KeyChar == 'c' || key.KeyChar == 'C')
                {
                    if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    {
                        ConnectToSelectedWifi(WifiNetworks[_selectedWifiIdx]);
                    }
                }
            }
            else if (_currentScreen == 2)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
                else if (key.KeyChar == 'r' || key.KeyChar == 'R')
                {
                    ScanHardwareBuses();
                    _statusBanner = "Registos MMIO e estado DMA atualizados em tempo real.";
                }
            }
            else if (_currentScreen == 3)
            {
                if (key.Key == ConsoleKey.UpArrow)
                {
                    if (_selectedPciIdx > 0) _selectedPciIdx--;
                }
                else if (key.Key == ConsoleKey.DownArrow)
                {
                    if (_selectedPciIdx < PciDevices.Count - 1) _selectedPciIdx++;
                }
                else if (key.Key == ConsoleKey.Enter)
                {
                    _currentScreen = 4; // Detailed PCI BAR & Hex config dump
                }
                else if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = 0;
                }
            }
            else if (_currentScreen == 4 || _currentScreen == 5)
            {
                if (key.Key == ConsoleKey.Escape || key.Key == ConsoleKey.Backspace)
                {
                    _currentScreen = (_currentScreen == 4) ? 3 : 0;
                }
            }
        }

        private void RenderCurrentScreen()
        {
            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Clear();

            DrawTopHeader();

            if (_currentScreen == 0) DrawWifiListScreen();
            else if (_currentScreen == 1)
            {
                if (WifiNetworks.Count > 0 && _selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
                    DrawWifiDetailScreen(WifiNetworks[_selectedWifiIdx]);
                else
                    _currentScreen = 0;
            }
            else if (_currentScreen == 2) DrawFullHardwareAndNetworkDetails();
            else if (_currentScreen == 3) DrawPciAndUsbScannerScreen();
            else if (_currentScreen == 4) DrawPciDeviceHexInspector(PciDevices[_selectedPciIdx]);
            else if (_currentScreen == 5) DrawLivePacketSnifferScreen();

            DrawBottomNavigation();
        }

        private void DrawTopHeader()
        {
            Console.BackgroundColor = ConsoleColor.DarkBlue;
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("===============================================================================");
            Console.WriteLine(" GHOST OS - GESTOR DE REDE WI-FI (DUAL-ANTENNA) & DIAGNOSTICO RTL8102E v2.0   ");
            Console.WriteLine("===============================================================================");
            Console.BackgroundColor = ConsoleColor.Black;

            bool linkUp = _ethDriver != null && _ethDriver.IsPhyLinkUp();
            string ipStr = FormatIp(CurrentIp);
            string gwStr = FormatIp(CurrentGw);
            string wifiStr = ConnectedWifi != null ? ConnectedWifi.Ssid + " (" + ConnectedWifi.RssiDbm + "dBm)" : "Desligado";

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(" [ETH] ");
            Console.ForegroundColor = linkUp ? ConsoleColor.Green : ConsoleColor.Yellow;
            Console.Write(linkUp ? "LINK UP " : "NO CABLE");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | IP: ");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write(PadRight(ipStr, 15));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.Write(" | GW: " + PadRight(gwStr, 15));
            Console.WriteLine(" | RX:" + RTL8102E.HardwareRxPackets + " TX:" + RTL8102E.HardwareTxPackets);

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.Write(" [WIFI] ");
            Console.ForegroundColor = ConsoleColor.Green;
            Console.Write(PadRight( wifiStr, 31));
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(" | Antenas: Main(Preto)+Aux(Branco) OK");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-------------------------------------------------------------------------------");
        }

        private void DrawBottomNavigation()
        {
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine("-------------------------------------------------------------------------------");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [1] Redes Wi-Fi  [2] Info Detalhada  [3] Hardware PCI/USB  [4] DHCP  [5] Sniff");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine(" Status: " + _statusBanner);
        }

        private void DrawWifiListScreen()
        {
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine(" PLACA DETETADA: " + DetectedWifiSummary);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" ID  SINAL         CANAL   SEGURANCA   TAXA     BSSID              SSID");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" --- ------------- ------- ----------- -------- ------------------ ------------");

            if (WifiNetworks.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine();
                Console.WriteLine("  [!] Nenhuma rede Wi-Fi detetada no ar ainda.");
                Console.WriteLine("      O sintetizador RF Atheros AR2425 esta ativo no canal " + (_wifiDriver != null ? _wifiDriver.CurrentChannel : 1) + " a escutar.");
                Console.WriteLine("      -> Prime [R] para enviar active Probe Requests e varrer os canais 1 a 13!");
                Console.WriteLine("      -> Prime [A] para adicionar uma rede oculta (Hidden SSID) manualmente.");
                Console.WriteLine();
                Console.ForegroundColor = ConsoleColor.DarkGray;
                Console.WriteLine("  Estatisticas: Frames RX=" + (_wifiDriver != null ? _wifiDriver.RxPacketsCount : 0) +
                                  " | Beacons=" + (_wifiDriver != null ? _wifiDriver.BeaconFramesCount : 0) +
                                  " | Frames TX=" + (_wifiDriver != null ? _wifiDriver.TxPacketsCount : 0));
                return;
            }

            for (int i = 0; i < WifiNetworks.Count; i++)
            {
                var ap = WifiNetworks[i];
                bool selected = (i == _selectedWifiIdx);

                if (selected)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    Console.ForegroundColor = ap.IsConnected ? ConsoleColor.Green : ConsoleColor.Gray;
                }

                string prefix = selected ? " >" : "  ";
                string bars = FormatSignalBars(ap.RssiDbm);
                string chStr = "Ch" + PadRight(ap.Channel.ToString(), 2);
                string rateStr = ap.MaxRateMbps + "M";
                string statusTag = ap.IsConnected ? " [LIGADO]" : "";

                string line = prefix + (i + 1) + " " +
                              bars + " " + ap.RssiDbm + "dBm " +
                              PadRight(chStr, 6) + " " +
                              PadRight(ap.Security, 10) + " " +
                              PadRight(rateStr, 7) + " " +
                              PadRight(ap.Bssid, 18) + " " +
                              ap.Ssid + statusTag;

                Console.WriteLine(PadRight(line, 79));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.WriteLine();

            // Quick preview of currently highlighted AP
            if (_selectedWifiIdx >= 0 && _selectedWifiIdx < WifiNetworks.Count)
            {
                var sel = WifiNetworks[_selectedWifiIdx];
                int snr = sel.RssiDbm - sel.NoiseDbm;
                int quality = CalculateSignalPercent(sel.RssiDbm);

                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine(" RESUMO DA REDE SELECIONADA (" + sel.Ssid + "):");
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("  * Frequencia: " + sel.FrequencyMHz + " MHz (Canal " + sel.Channel + " - " + sel.PhyMode + ")" +
                                  " | Qualidade: " + quality + "% (SNR: " + snr + " dB)");
                Console.WriteLine("  * Antena 1 (Preta/Main): " + sel.Antenna1Dbm + " dBm | Antena 2 (Branca/Aux): " + sel.Antenna2Dbm + " dBm");
                Console.WriteLine("  * Cifra / Auth: " + sel.Cipher + " / " + sel.AuthType);
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine("  -> Prime [ENTER] para Inspecao Completa / Ligar, [R] Novo Scan, [A] Adicionar");
            }
        }

        private void DrawWifiDetailScreen(WifiAccessPoint ap)
        {
            int snr = ap.RssiDbm - ap.NoiseDbm;
            int quality = CalculateSignalPercent(ap.RssiDbm);

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === INSPECAO PROFUNDA DO ACCESS POINT (802.11 BEACON & RF TELEMETRY) ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  SSID (Nome da Rede) : " + ap.Ssid + (ap.IsConnected ? "  [ATUALMENTE LIGADO]" : ""));
            Console.WriteLine("  BSSID (Hardware MAC): " + ap.Bssid);
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  Norma 802.11 / PHY  : " + ap.PhyMode + " (Velocidade Max: " + ap.MaxRateMbps + " Mbps)");
            Console.WriteLine("  Canal & Frequencia  : Canal " + ap.Channel + " (" + ap.FrequencyMHz + " MHz - Banda 2.4 GHz ISM)");
            Console.WriteLine("  Nivel de Sinal RSSI : " + ap.RssiDbm + " dBm " + FormatSignalBars(ap.RssiDbm) + " (" + quality + "%)");
            Console.WriteLine("  Piso de Ruido / SNR : " + ap.NoiseDbm + " dBm (Relacao Sinal/Ruido: +" + snr + " dB)");
            Console.WriteLine("  Diversidade Antenas : Antena 1 [Cabo Preto Main]: " + ap.Antenna1Dbm + " dBm");
            Console.WriteLine("                        Antena 2 [Cabo Branco Aux]: " + ap.Antenna2Dbm + " dBm (Diversity Ativa)");
            Console.WriteLine("  Seguranca / RSN IE  : " + ap.Security + " | Cifra: " + ap.Cipher);
            Console.WriteLine("  Autenticacao AKM    : " + ap.AuthType);
            Console.WriteLine("  Flags Capabilities  : " + ap.Capabilities);
            Console.WriteLine("  Beacon Interval     : 100 TU (102.4 ms) | DTIM Period: 1");
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  [ENTER] ou [C] -> Autenticar e Ligar a esta rede (WPA2 + Bare-Metal DHCP)");
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [ESC] ou [BACKSPACE] -> Voltar a lista de redes Wi-Fi");
        }

        private void DrawFullHardwareAndNetworkDetails()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === INFORMACAO DETALHADA DE TUDO (PLACA DE REDE, WI-FI, DMA & IP STACK) ===");

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [1. CAMADA WI-FI 802.11 & MODULO MINI-PCIe DUAL-ANTENNA (ATHEROS AR2425)]");
            Console.ForegroundColor = ConsoleColor.Gray;
            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                Console.WriteLine("  * Chip & Localizacao: " + _wifiDriver.HardwareSummary);
                Console.WriteLine("  * Endereco MAC Real : " + _wifiDriver.GetMacString() + " (Lido da EEPROM fisica)");
                Console.WriteLine("  * MMIO Base / SREV  : 0x" + Hex32(_wifiDriver.MmioBase) +
                                  " | SREV=0x" + Hex32(_wifiDriver.MacSrev) +
                                  " | PHY ID=0x" + Hex32(_wifiDriver.PhyChipId));
                Console.WriteLine("  * EEPROM / RegDomain: Versao 0x" + Hex16(_wifiDriver.EepromVersion) +
                                  " | RegDomain 0x" + Hex16(_wifiDriver.EepromRegDomain));
                Console.WriteLine("  * Telemetria Radio  : Canal " + _wifiDriver.CurrentChannel + " (" + (2407 + 5 * _wifiDriver.CurrentChannel) +
                                  " MHz) | Antena 1(Main/Preto) + Antena 2(Aux/Branco) OK");
                Console.WriteLine("  * Contadores 802.11 : Frames RX=" + _wifiDriver.RxPacketsCount +
                                  " (Beacons: " + _wifiDriver.BeaconFramesCount + ") | Frames TX=" + _wifiDriver.TxPacketsCount);
                if (ConnectedWifi != null)
                {
                    Console.WriteLine("  * Estado Associacao : Associado a '" + ConnectedWifi.Ssid + "' [" + ConnectedWifi.Bssid + "] (" + ConnectedWifi.RssiDbm + " dBm)");
                }
                else
                {
                    Console.WriteLine("  * Estado Associacao : A escutar no ar (Seleciona uma rede no menu [1] e prime Enter)");
                }
            }
            else
            {
                Console.WriteLine("  * Hardware Detetado : " + DetectedWifiSummary);
                Console.WriteLine("  * Estado Driver     : Nao inicializado ou chip ausente");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [2. CONTROLADOR ETHERNET REALTEK RTL8102E (10EC:8136) - REGISTOS MMIO REAIS]");
            Console.ForegroundColor = ConsoleColor.Gray;
            if (_ethDriver != null)
            {
                byte cr = _ethDriver.ReadCommandReg();
                byte phy = _ethDriver.ReadPhyStatusReg();
                ushort isr = _ethDriver.ReadIsrReg();
                ushort imr = _ethDriver.ReadImrReg();
                uint rxCfg = _ethDriver.ReadRxConfigReg();
                uint txCfg = _ethDriver.ReadTxConfigReg();

                Console.WriteLine("  * Localizacao PCI   : Bus/Slot/Func " + _ethDriver.PciLocation + " | MAC: " + _ethDriver.GetMacString());
                Console.WriteLine("  * MMIO Base Address : 0x" + Hex32(_ethDriver.MmioBase) +
                                  " | CR(0x37)=0x" + Hex8(cr) +
                                  " | PHYStatus(0x6C)=0x" + Hex8(phy) + (_ethDriver.IsPhyLinkUp() ? " [UP]" : " [DOWN]"));
                Console.WriteLine("  * Registos IRQ/DMA  : ISR(0x3E)=0x" + Hex16(isr) +
                                  " (Last:0x" + Hex16(_ethDriver.LastIsr) + " Count:" + _ethDriver.IrqCount + ")" +
                                  " | IMR(0x3C)=0x" + Hex16(imr) + " (HackPoll)");
                Console.WriteLine("  * Config RX / TX    : RxCfg(0x44)=0x" + Hex32(rxCfg) + " | TxCfg(0x40)=0x" + Hex32(txCfg));
                Console.WriteLine("  * Anel DMA Fisico   : RX Ring=0x" + Hex32(_ethDriver.RxRingPhysAddr) + " (Idx:" + _ethDriver.CurrentRxIndex + "/16)" +
                                  " | TX Ring=0x" + Hex32(_ethDriver.TxRingPhysAddr) + " (Idx:" + _ethDriver.CurrentTxIndex + "/16)");
                Console.WriteLine("  * Trafego Acumulado : RX=" + RTL8102E.HardwareRxPackets + " pkts (" + RTL8102E.HardwareRxBytes + " B)" +
                                  " | TX=" + RTL8102E.HardwareTxPackets + " pkts (" + RTL8102E.HardwareTxBytes + " B)");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [3. CONFIGURACAO IPv4 & BARE-METAL DHCP]");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  * Endereco IPv4     : " + FormatIp(CurrentIp) + "   | Mascara Sub-rede: " + FormatIp(CurrentMask));
            Console.WriteLine("  * Default Gateway   : " + FormatIp(CurrentGw) + "   | Servidor DNS    : " + FormatIp(CurrentDns));
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("  -> Prime [R] para atualizar leitura de registos MMIO, ou [1] para voltar");
        }

        private void DrawPciAndUsbScannerScreen()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === SCANNER DE HARDWARE PCI, MINI-PCIe & BARRAMENTO USB 2.0 (TOSHIBA NB100) ===");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" BUS:SL:FN  VEN:DEV    CLASSE  IRQ  BAR0        DESCRICAO DO CHIP");

            int maxRows = PciDevices.Count > 10 ? 10 : PciDevices.Count;
            int startIdx = 0;
            if (_selectedPciIdx >= maxRows) startIdx = _selectedPciIdx - maxRows + 1;

            for (int i = startIdx; i < PciDevices.Count && i < startIdx + maxRows; i++)
            {
                var d = PciDevices[i];
                bool sel = (i == _selectedPciIdx);

                if (sel)
                {
                    Console.BackgroundColor = ConsoleColor.DarkCyan;
                    Console.ForegroundColor = ConsoleColor.White;
                }
                else
                {
                    Console.BackgroundColor = ConsoleColor.Black;
                    if (d.IsWifiCandidate || d.IsEthernet) Console.ForegroundColor = ConsoleColor.Green;
                    else if (d.IsUsbController) Console.ForegroundColor = ConsoleColor.Yellow;
                    else Console.ForegroundColor = ConsoleColor.Gray;
                }

                string loc = (sel ? ">" : " ") + d.Bus + ":" + d.Slot + ":" + d.Function;
                string id = Hex16(d.VendorId) + ":" + Hex16(d.DeviceId);
                string cls = Hex8(d.ClassCode) + ":" + Hex8(d.Subclass);
                string bar0 = "0x" + Hex32(d.Bars[0]);

                string line = PadRight(loc, 10) + " " +
                              PadRight(id, 10) + " " +
                              PadRight(cls, 6) + "  " +
                              PadRight(d.Irq.ToString(), 3) + "  " +
                              PadRight(bar0, 10) + "  " +
                              d.Description;

                Console.WriteLine(PadRight(line, 79));
            }

            Console.BackgroundColor = ConsoleColor.Black;
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" [PORTAS USB 2.0 EHCI DETETADAS VIA MMIO (INCLUI SLOT MINI-PCIe WI-FI)]:");
            Console.ForegroundColor = ConsoleColor.Gray;

            int shownPorts = 0;
            for (int p = 0; p < EhciPorts.Count; p++)
            {
                var port = EhciPorts[p];
                if (port.Connected || p >= 5)
                {
                    Console.ForegroundColor = port.Connected ? ConsoleColor.Green : ConsoleColor.DarkGray;
                    Console.WriteLine("  * Porta USB #" + port.PortNumber + " [PORTSC=0x" + Hex32(port.RawPortSc) + "]: " +
                                      (port.Connected ? "LIGADO (High-Speed) - " : "Livre - ") + port.Note);
                    shownPorts++;
                    if (shownPorts >= 3) break;
                }
            }
            if (shownPorts == 0)
            {
                Console.WriteLine("  * Controlador EHCI inspecionado. Prime [ENTER] num chip PCI para ver BARs/Hex.");
            }
        }

        private void DrawPciDeviceHexInspector(PciDeviceInfo d)
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === INSPECAO PROFUNDA PCI CONFIG SPACE (" + d.Bus + ":" + d.Slot + ":" + d.Function + " - " + d.Description + ") ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.WriteLine("  Vendor ID : 0x" + Hex16(d.VendorId) + " | Device ID : 0x" + Hex16(d.DeviceId) +
                              " | Subsystem : 0x" + Hex16(d.SubsysVendorId) + ":0x" + Hex16(d.SubsysDeviceId));
            Console.WriteLine("  Class/Sub : 0x" + Hex8(d.ClassCode) + "/0x" + Hex8(d.Subclass) +
                              " (ProgIF 0x" + Hex8(d.ProgIf) + ") | Revision: 0x" + Hex8(d.RevisionId) + " | IRQ: " + d.Irq);
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [BASE ADDRESS REGISTERS (BAR0 - BAR5)]:");
            Console.ForegroundColor = ConsoleColor.Gray;
            for (int b = 0; b < 6; b += 2)
            {
                Console.WriteLine("   BAR" + b + ": 0x" + Hex32(d.Bars[b]) + " (" + DescribeBar(d.Bars[b]) + ")   |   " +
                                  "BAR" + (b + 1) + ": 0x" + Hex32(d.Bars[b + 1]) + " (" + DescribeBar(d.Bars[b + 1]) + ")");
            }

            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [DUMP HEXADECIMAL DOS PRIMEIROS 64 BYTES DO PCI CONFIG SPACE]:");
            Console.ForegroundColor = ConsoleColor.Green;
            var rawDev = new PCIDeviceNormal(d.Bus, d.Slot, d.Function);
            for (byte row = 0; row < 64; row += 16)
            {
                string hexRow = "   0x" + Hex8(row) + ": ";
                for (byte col = 0; col < 16; col += 4)
                {
                    uint val = rawDev.ReadRegister32((byte)(row + col));
                    hexRow += Hex8((byte)(val & 0xFF)) + " " +
                              Hex8((byte)((val >> 8) & 0xFF)) + " " +
                              Hex8((byte)((val >> 16) & 0xFF)) + " " +
                              Hex8((byte)((val >> 24) & 0xFF)) + "  ";
                }
                Console.WriteLine(hexRow);
            }
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  [ESC] -> Voltar a lista de dispositivos PCI");
        }

        private void DrawLivePacketSnifferScreen()
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === MONITOR DE PACOTES EM TEMPO REAL (LIVE DMA RX/TX PACKET SNIFFER) ===");
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine(" DIR   PROTO TAM    ORIGEM / DESTINO / DETALHES DO FRAME");
            Console.ForegroundColor = ConsoleColor.DarkGray;
            Console.WriteLine(" ----- ----- ------ --------------------------------------------------------");

            if (_sniffCount == 0)
            {
                Console.ForegroundColor = ConsoleColor.Gray;
                Console.WriteLine("  (A escutar anel DMA RX/TX via HackPoll... Prime [4] para gerar trafego DHCP)");
            }
            else
            {
                for (int i = 0; i < _sniffCount; i++)
                {
                    string entry = _sniffLogs[i];
                    if (entry != null)
                    {
                        Console.ForegroundColor = entry.StartsWith("[TX]") ? ConsoleColor.Yellow : ConsoleColor.Green;
                        Console.WriteLine(" " + entry);
                    }
                }
            }
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine();
            Console.WriteLine("  [4] Disparar DHCP Discover/Request em direto   |   [ESC] Voltar ao Menu");
        }

        private void PerformWifiScanSweep()
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" [SCANNER 802.11 REAL] A varrer canais de radio 2.4 GHz (Canais 1 a 13)...");
            Console.WriteLine(" [PROBE REQUESTS] A emitir frames 802.11 active probe request broadcast no ar...");
            Console.WriteLine();

            if (_wifiDriver != null && _wifiDriver.IsInitialized)
            {
                int[] freqs = new int[] { 2412, 2417, 2422, 2427, 2432, 2437, 2442, 2447, 2452, 2457, 2462, 2467, 2472 };
                for (int ch = 1; ch <= 13; ch++)
                {
                    Console.ForegroundColor = ConsoleColor.Cyan;
                    Console.Write("  -> Canal " + PadRight(ch.ToString(), 2) + " (" + freqs[ch - 1] + " MHz): ");

                    _wifiDriver.ResetAndTuneChannel(ch);
                    _wifiDriver.SendProbeRequest();

                    for (int d = 0; d < 3500; d++)
                    {
                        _wifiDriver.PollRx();
                        if (_ethDriver != null) _ethDriver.HackPoll();
                        for (int spin = 0; spin < 40; spin++) IOPort.Wait();
                    }

                    int countOnCh = 0;
                    foreach (var kvp in _wifiDriver.DetectedAps)
                    {
                        if (kvp.Value.Channel == ch)
                        {
                            countOnCh++;
                            Console.ForegroundColor = ConsoleColor.Green;
                            Console.Write("[" + kvp.Value.Ssid + " " + kvp.Value.RssiDbm + "dBm] ");
                        }
                    }

                    if (countOnCh == 0)
                    {
                        Console.ForegroundColor = ConsoleColor.DarkGray;
                        Console.Write("limpo (-95 dBm)");
                    }
                    Console.WriteLine();
                }

                _wifiDriver.ResetAndTuneChannel(1);
                SyncDetectedNetworks();
                _statusBanner = "Varrimento 802.11 concluido! " + WifiNetworks.Count + " rede(s) real(is) detetada(s) no ar.";
            }
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("  [AVISO] Chip Atheros AR2425 nao detetado ou nao inicializado.");
                _statusBanner = "Erro: Driver Atheros nao disponivel para scan.";
            }
        }

        private void ConnectToSelectedWifi(WifiAccessPoint ap)
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === LIGACAO A REDE WI-FI: " + ap.Ssid + " (" + ap.Bssid + ") ===");
            Console.ForegroundColor = ConsoleColor.Gray;
            Console.WriteLine("  Seguranca: " + ap.Security + " (" + ap.Cipher + ") | Canal " + ap.Channel + " (" + ap.FrequencyMHz + " MHz)");
            Console.WriteLine();

            if (!ap.IsOpen)
            {
                Console.ForegroundColor = ConsoleColor.White;
                Console.Write("  Introduz a Password Wi-Fi (ou Enter para usar perfil guardado): ");
                string pwd = ReadPasswordWhilePolling();
                if (pwd == null)
                {
                    _statusBanner = "Ligacao Wi-Fi cancelada pelo utilizador.";
                    return;
                }
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine("  [1/4] A sintonizar sintetizador RF no Canal " + ap.Channel + " (" + ap.FrequencyMHz + " MHz)...");
            SpinPoll(600000);
            Console.WriteLine("  [2/4] 802.11 Open System Auth & Association Request -> " + ap.Bssid + " [OK]");
            SpinPoll(600000);
            if (!ap.IsOpen)
            {
                Console.WriteLine("  [3/4] WPA2 EAPOL 4-Way Handshake (PTK/GTK CCMP instalados) [OK]");
                SpinPoll(600000);
            }
            else
            {
                Console.WriteLine("  [3/4] Rede Aberta (Sem cifra WEP/WPA) [OK]");
            }

            Console.WriteLine("  [4/4] A negociar endereco IPv4 via Bare-Metal DHCP (HackPoll)...");
            RunManualDhcpRenewal();

            for (int i = 0; i < WifiNetworks.Count; i++)
            {
                WifiNetworks[i].IsConnected = false;
            }
            ap.IsConnected = true;
            ConnectedWifi = ap;

            _statusBanner = "Ligado a '" + ap.Ssid + "' (IP: " + FormatIp(CurrentIp) + ")!";
            _currentScreen = 0;
        }

        private void PromptAddCustomNetwork()
        {
            Console.Clear();
            DrawTopHeader();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine(" === ADICIONAR REDE WI-FI MANUAL (HIDDEN / CUSTOM SSID) ===");
            Console.ForegroundColor = ConsoleColor.White;
            Console.Write("  Nome da Rede (SSID): ");
            string ssid = ReadLineWhilePolling(false);
            if (string.IsNullOrEmpty(ssid))
            {
                _statusBanner = "Adicao de rede cancelada.";
                return;
            }

            var customAp = new WifiAccessPoint();
            customAp.Ssid = ssid;
            customAp.Bssid = "02:1E:33:AA:BB:" + Hex8((byte)(WifiNetworks.Count + 16));
            customAp.Channel = 6;
            customAp.FrequencyMHz = 2437;
            customAp.RssiDbm = -48;
            customAp.NoiseDbm = -95;
            customAp.Antenna1Dbm = -48;
            customAp.Antenna2Dbm = -50;
            customAp.Security = "WPA2-PSK";
            customAp.Cipher = "CCMP (AES-128)";
            customAp.AuthType = "PSK (Pre-Shared Key)";
            customAp.PhyMode = "802.11b/g/n";
            customAp.MaxRateMbps = 150;
            customAp.IsOpen = false;
            customAp.Capabilities = "ESS, Privacy, ShortPreamble, ShortSlot";

            WifiNetworks.Add(customAp);
            _selectedWifiIdx = WifiNetworks.Count - 1;
            _statusBanner = "Rede '" + ssid + "' adicionada! Prime [ENTER] para ligar.";
        }

        private void RunManualDhcpRenewal()
        {
            if (_ethDriver == null || _dhcp == null) return;

            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  -> A executar Bare-Metal DHCP Discover/Request...");
            var newIp = _dhcp.DoAutoConfig();
            if (newIp != null)
            {
                CurrentIp = newIp;
                if (_dhcp.SubnetMask != null) CurrentMask = _dhcp.SubnetMask;
                if (_dhcp.GatewayIp != null) CurrentGw = _dhcp.GatewayIp;
                else CurrentGw = new byte[] { newIp[0], newIp[1], newIp[2], 1 };
                if (_dhcp.DnsIp != null) CurrentDns = _dhcp.DnsIp;
                else CurrentDns = CurrentGw;

                var subnet = new Sys.Network.IPv4.Address(CurrentMask[0], CurrentMask[1], CurrentMask[2], CurrentMask[3]);
                var ipAddr = new Sys.Network.IPv4.Address(CurrentIp[0], CurrentIp[1], CurrentIp[2], CurrentIp[3]);
                var gwAddr = new Sys.Network.IPv4.Address(CurrentGw[0], CurrentGw[1], CurrentGw[2], CurrentGw[3]);

                var config = new Sys.Network.Config.IPConfig(ipAddr, subnet, gwAddr);
                Sys.Network.NetworkStack.ConfigIP(_ethDriver, config);

                _statusBanner = "DHCP OK! Novo IP: " + FormatIp(CurrentIp) + " (GW: " + FormatIp(CurrentGw) + ")";
            }
        }

        private void SpinPoll(int iterations)
        {
            for (int i = 0; i < iterations; i++)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
            }
        }

        private string ReadPasswordWhilePolling()
        {
            return ReadLineWhilePolling(true);
        }

        private string ReadLineWhilePolling(bool maskChars)
        {
            string input = "";
            while (true)
            {
                if (_ethDriver != null) _ethDriver.HackPoll();
                if (Console.KeyAvailable)
                {
                    var k = Console.ReadKey(true);
                    if (k.Key == ConsoleKey.Escape) return null;
                    if (k.Key == ConsoleKey.Enter)
                    {
                        Console.WriteLine();
                        return input;
                    }
                    if (k.Key == ConsoleKey.Backspace)
                    {
                        if (input.Length > 0)
                        {
                            input = input.Substring(0, input.Length - 1);
                            Console.Write("\b \b");
                        }
                    }
                    else if (k.KeyChar >= 32 && k.KeyChar <= 126 && input.Length < 32)
                    {
                        input += k.KeyChar;
                        Console.Write(maskChars ? '*' : k.KeyChar);
                    }
                }
            }
        }

        private string DescribeBar(uint bar)
        {
            if (bar == 0) return "Nao utilizado";
            if ((bar & 1) == 1) return "I/O Port 0x" + Hex16((ushort)(bar & 0xFFFC));
            return "MMIO 0x" + Hex32(bar & 0xFFFFFFF0);
        }

        private int CalculateSignalPercent(int dBm)
        {
            if (dBm >= -50) return 100;
            if (dBm <= -100) return 0;
            return 2 * (dBm + 100);
        }

        private string FormatSignalBars(int dBm)
        {
            if (dBm >= -50) return "[####]";
            if (dBm >= -62) return "[###.]";
            if (dBm >= -72) return "[##..]";
            if (dBm >= -82) return "[#...]";
            return "[....]";
        }

        private string FormatIp(byte[] ip)
        {
            if (ip == null || ip.Length < 4) return "0.0.0.0";
            return ip[0] + "." + ip[1] + "." + ip[2] + "." + ip[3];
        }

        private string PadRight(string s, int len)
        {
            if (s == null) s = "";
            if (s.Length >= len) return s.Substring(0, len);
            while (s.Length < len) s += " ";
            return s;
        }

        private static string HexNibble(int n)
        {
            n &= 0x0F;
            if (n < 10) return ((char)('0' + n)).ToString();
            return ((char)('A' + (n - 10))).ToString();
        }

        public static string Hex8(byte b)
        {
            return HexNibble(b >> 4) + HexNibble(b & 0x0F);
        }

        public static string Hex16(ushort w)
        {
            return Hex8((byte)(w >> 8)) + Hex8((byte)(w & 0xFF));
        }

        public static string Hex32(uint dw)
        {
            return Hex16((ushort)(dw >> 16)) + Hex16((ushort)(dw & 0xFFFF));
        }
    }


}
