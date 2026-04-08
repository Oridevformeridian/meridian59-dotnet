using Microsoft.VisualStudio.TestTools.UnitTesting;
using Meridian59.Files;
using Meridian59.Files.ROO;
using System.IO;

namespace Meridian59.UnitTest.Files
{
    [TestClass]
    public class ResourceManagerTest
    {
        [TestMethod]
        public void TestLoadRooFile()
        {
            string projectRoot = "/var/home/mycroft/src/meridian59-dotnet";
            string resourcesPath = Path.Combine(projectRoot, "Resources");
            
            ResourceManager rm = new ResourceManager();
            rm.Init(
                Path.Combine(resourcesPath, "strings"),
                Path.Combine(resourcesPath, "rooms"),
                Path.Combine(resourcesPath, "bgfobjects"),
                Path.Combine(resourcesPath, "bgftextures"),
                Path.Combine(resourcesPath, "sounds"),
                Path.Combine(resourcesPath, "music"),
                Path.Combine(resourcesPath, "mails")
            );

            RooFile razainn = rm.GetRoom("razainn.roo");
            
            Assert.IsNotNull(razainn, "RooFile razainn.roo should not be null");
            Assert.IsTrue(razainn.Walls.Count > 0, "RooFile should have walls");
            Assert.AreEqual("razainn", razainn.Filename);
        }
    }
}
