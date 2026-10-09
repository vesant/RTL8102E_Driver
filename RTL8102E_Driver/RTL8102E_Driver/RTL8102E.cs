using System;
using Cosmos.Core;
using Cosmos.HAL;

namespace RTL8102E_Driver
{
    public class RTL8102E : NetworkDevice
    {
        private PCIDeviceNormal pciDevice;
        private uint mmioBase;
        private MemoryBlock mmio;
        private byte[] macBytes = new byte[6];

        private ManagedMemoryBlock rxDescBlock;
        private ManagedMemoryBlock txDescBlock;
        private ManagedMemoryBlock[] rxBuffers = new ManagedMemoryBlock[4];
        private ManagedMemoryBlock[] txBuffers = new ManagedMemoryBlock[4];

        // NetworkDevice Properties
        public override CardType CardType => CardType.Ethernet;
        public override Cosmos.HAL.Network.MACAddress MACAddress => new Cosmos.HAL.Network.MACAddress(macBytes);
        public override string Name => "RTL8102E";
        public override bool Ready => true;
        
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

        private bool Phase3DescriptorRings()
        {
            Console.WriteLine("r8102e: initializing dma descriptor rings...");

            // Allocate 4 descriptors for RX and TX (4 * 16 = 64 bytes), aligned to 256
            rxDescBlock = new ManagedMemoryBlock(64, 256);
            txDescBlock = new ManagedMemoryBlock(64, 256);

            // Allocate RX Buffers (4 buffers of 1536 bytes -> aligned to 8 bytes)
            rxBuffers = new ManagedMemoryBlock[4];
            for (int i = 0; i < 4; i++)
            {
                rxBuffers[i] = new ManagedMemoryBlock(1536, 8);
            }

            // Allocate TX Buffers (4 buffers of 1536 bytes -> aligned to 8 bytes)
            txBuffers = new ManagedMemoryBlock[4];
            for (int i = 0; i < 4; i++)
            {
                txBuffers[i] = new ManagedMemoryBlock(1536, 8);
            }

            Console.WriteLine("r8102e: setting up rx ring in ram...");
            for (int i = 0; i < 4; i++)
            {
                uint offset = (uint)(i * 16);
                
                // Command/Status: 1536 buffer size (bits 0-13) | OWN bit (bit 31)
                // If it's the last descriptor, set EOR bit (bit 30)
                uint cmdStatus = 1536 | 0x80000000;
                if (i == 3) cmdStatus |= 0x40000000;
                
                Write32(rxDescBlock, offset, cmdStatus);
                Write32(rxDescBlock, offset + 4, 0); // Vlan
                Write32(rxDescBlock, offset + 8, (uint)rxBuffers[i].Offset); // Buffer Address Low
                Write32(rxDescBlock, offset + 12, (uint)(rxBuffers[i].Offset >> 32)); // Buffer Address High
            }

            Console.WriteLine("r8102e: setting up tx ring in ram...");
            for (int i = 0; i < 4; i++)
            {
                uint offset = (uint)(i * 16);
                Write32(txDescBlock, offset, 0);
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
            Console.WriteLine("r8102e: skipping hardware IRQ binding (switching to polling mode)...");

            Console.WriteLine("r8102e: enabling RX and TX in CR...");
            // Command Register (0x37): RE=0x08, TE=0x04 -> 0x0C
            mmio.Bytes[0x37] = 0x0C;

            Console.WriteLine("r8102e: masking all hardware interrupts in IMR (we will poll instead)...");
            // Interrupt Mask Register (0x3C): 0x0000 ensures no hardware INTA# is generated
            mmio.Words[0x3C] = 0x0000;

            Console.WriteLine("r8102e: driver fully initialized and listening!");
            return true;
        }

        private int currentTxDesc = 0;

        public override bool QueueBytes(byte[] buffer, int offset, int length)
        {
            uint descOffset = (uint)(currentTxDesc * 16);
            uint cmdStatus = Read32(txDescBlock, descOffset);
            
            // Check if MAC still owns the descriptor
            if ((cmdStatus & 0x80000000) != 0)
                return false; // Queue full!

            // Copy data to txBuffers[currentTxDesc]
            for (int i = 0; i < length; i++)
            {
                txBuffers[currentTxDesc][(uint)i] = buffer[offset + i];
            }

            // Ethernet requires a minimum payload of 60 bytes (excluding CRC).
            // ARP replies are only 42 bytes. RTL8102E does NOT auto-pad runts!
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
            if (currentTxDesc == 3) newCmdStatus |= 0x40000000; // Preserve EOR bit
            
            Write32(txDescBlock, descOffset, newCmdStatus);
            
            // Trigger TX poll (TxPoll register 0x38 = 0x40)
            mmio.Bytes[0x38] = 0x40;
            
            Console.WriteLine($"[TX] Sent {txLength} bytes to MAC (Desc {currentTxDesc}) - original {length}");
            
            currentTxDesc = (currentTxDesc + 1) % 4;
            return true;
        }

        public void Poll()
        {
            // 1. Process RX Ring
            while (true)
            {
                uint offset = (uint)(currentRxDesc * 16);
                uint cmdStatus = Read32(rxDescBlock, offset);
                
                // If OWN bit (bit 31) is 1, MAC still owns it, ring is empty for us
                if ((cmdStatus & 0x80000000) != 0)
                    break;
                    
                // Bit 31 is 0! MAC transferred a packet to RAM!
                // Realtek includes the 4-byte Ethernet CRC in the length. Cosmos doesn't want it.
                uint rawLength = cmdStatus & 0x3FFF;
                uint length = rawLength > 4 ? rawLength - 4 : rawLength; 
                
                // Read into managed array for Cosmos NetworkStack
                byte[] packet = new byte[length];
                for (uint i = 0; i < length; i++)
                {
                    packet[i] = rxBuffers[currentRxDesc][i];
                }

                // Send to NetworkStack
                if (DataReceived != null)
                {
                    Console.WriteLine($"[RX] Passing {length} bytes to NetworkStack");
                    DataReceived(packet);
                }
                
                // Give descriptor back to MAC
                uint newCmdStatus = 1536 | 0x80000000;
                if (currentRxDesc == 3) newCmdStatus |= 0x40000000; // Preserve EOR bit on last desc
                
                Write32(rxDescBlock, offset, newCmdStatus);
                
                currentRxDesc = (currentRxDesc + 1) % 4;
            }

            // 2. Read and Clear Interrupt Status Register (0x3E)
            ushort status = mmio.Words[0x3E];
            if (status != 0)
            {
                LastIsr = status;
                IrqCount++;
                
                // Acknowledge by writing the exact same bits back to ISR
                mmio.Words[0x3E] = status;
            }
        }
    }
}
