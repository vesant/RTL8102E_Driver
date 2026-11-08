using System;
using System.Collections.Generic;
using System.Text;
using Sys = Cosmos.System;

namespace RTL8102E_Driver {
    public class Kernel : Sys.Kernel {
        private RTL8102E driver;

        protected override void BeforeRun() {
            Console.WriteLine("Cosmos kernel ready.");
            Console.WriteLine("loading r8102e driver module...");
            
            driver = new RTL8102E();
            bool result = driver.Initialize();
            
            if (result)
            {
                Console.WriteLine("r8102e: Driver ready! Initiating bare-metal DHCP...");
                
                var dhcp = new CustomDHCP(driver);
                var assignedIp = dhcp.DoAutoConfig();

                if (assignedIp != null)
                {
                    // Bind the newly grabbed IP and Gateway to the Cosmos NetworkStack
                    byte[] mask = dhcp.SubnetMask ?? new byte[] { 255, 255, 255, 0 };
                    byte[] gw = dhcp.GatewayIp ?? new byte[] { assignedIp[0], assignedIp[1], assignedIp[2], 1 };

                    var subnet = new Sys.Network.IPv4.Address(mask[0], mask[1], mask[2], mask[3]);
                    var ipAddr = new Sys.Network.IPv4.Address(assignedIp[0], assignedIp[1], assignedIp[2], assignedIp[3]);
                    var gwAddr = new Sys.Network.IPv4.Address(gw[0], gw[1], gw[2], gw[3]);
                    
                    var config = new Sys.Network.Config.IPConfig(ipAddr, subnet, gwAddr);
                    Sys.Network.NetworkStack.ConfigIP(driver, config);
                    
                    Console.WriteLine($"r8102e: NetworkStack bound successfully to {assignedIp[0]}.{assignedIp[1]}.{assignedIp[2]}.{assignedIp[3]} (Gateway: {gw[0]}.{gw[1]}.{gw[2]}.{gw[3]})!");
                }
                else
                {
                    Console.WriteLine("r8102e: DHCP failed. Cannot configure NetworkStack.");
                }
            }
            else
            {
                Console.WriteLine("r8102e: probe failed.");
            }
        }

        protected override void Run() {
            if (driver != null)
            {
                driver.HackPoll();
                Sys.Network.NetworkStack.Update();
            }
        }
    }
}
