using Spokes_Server.Core.Services.Communication;
using Spokes_Server.Core.Services.Projects;
using Spokes_Server.Core.Services.Core;
using System.IO;

namespace Spokes_Server.Tests
{
    public abstract class TestDataTestBase : IDisposable
    {
        protected readonly string _testDataPath;

        protected TestDataTestBase()
        {
            _testDataPath = Path.Combine(Path.GetTempPath(), "Spokes_Test_Data_" + Guid.NewGuid().ToString());
            Directory.CreateDirectory(_testDataPath);
        }

        public virtual void Dispose()
        {
            if (Directory.Exists(_testDataPath))
            {
                try
                {
                    Directory.Delete(_testDataPath, true);
                }
                catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"Cleanup error: {ex.Message}"); }
            }
        }
    }
}

