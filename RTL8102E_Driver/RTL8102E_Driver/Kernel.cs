using System;
using System.Collections.Generic;
using System.Text;
using Sys = Cosmos.System;

namespace RTL8102E_Driver {
    public class Kernel : Sys.Kernel {
        private RTL8102E driver;
        private uint lastIrqCount = 0;

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
                    // Bind the newly grabbed IP to the Cosmos NetworkStack
                    var subnet = new Sys.Network.IPv4.Address(255, 255, 255, 0); // Fake subnet for testing
                    var ipAddr = new Sys.Network.IPv4.Address(assignedIp[0], assignedIp[1], assignedIp[2], assignedIp[3]);
                    var gwAddr = new Sys.Network.IPv4.Address(assignedIp[0], assignedIp[1], assignedIp[2], 1); // Fake gateway
                    
                    var config = new Sys.Network.Config.IPConfig(ipAddr, subnet, gwAddr);
                    Sys.Network.NetworkStack.ConfigIP(driver, config);
                    
                    Console.WriteLine($"r8102e: NetworkStack bound successfully to {assignedIp[0]}.{assignedIp[1]}.{assignedIp[2]}.{assignedIp[3]} !");
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
                Sys.Network.NetworkStack.Update();
            }
        }
    }
}
