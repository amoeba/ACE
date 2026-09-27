using System;
using System.Collections.Generic;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using ACE.Common;
using ACE.DatLoader;
using ACE.Server.Managers;

namespace ACE.Server.Tests
{
    /// <summary>
    /// The guard that keeps a missing .dat file from taking the process down.
    /// <para>
    /// <see cref="WorldManager.Initialize"/> spawns
    /// <see cref="LandblockManager.PreloadConfigLandblocks"/> on a background thread.
    /// With no <c>client_cell_1.dat</c>, <see cref="DatManager.CellDat"/> is null and
    /// <c>Landblock..ctor</c> reads it unconditionally -- an unhandled null dereference
    /// on a background thread, which kills the whole process after the port is already
    /// listening. The guard in <c>PreloadConfigLandblocks</c> is what stops that.
    /// </para>
    /// <para>
    /// This calls it directly rather than on a background thread, so a missing guard is
    /// a test failure rather than a crashed host: the same defect, one layer closer and
    /// therefore observable. It deliberately does not call <c>TestEnvironment</c>, which
    /// would hit the same dereference through <c>WorldManager.Initialize</c>.
    /// </para>
    /// </summary>
    [TestClass]
    public class LandblockPreloadTests
    {
        [TestMethod]
        public void PreloadConfigLandblocks_DoesNotThrowWhenCellDatIsMissing()
        {
            var saved = ConfigManager.Config;

            try
            {
                ConfigManager.Initialize(new MasterConfiguration
                {
                    Server = new GameConfiguration
                    {
                        LandblockPreloading = true,
                        PreloadedLandblocks = new List<PreloadedLandblocks>
                        {
                            new PreloadedLandblocks
                            {
                                Id = "E74EFFFF",
                                Description = "Hebian-To (Global Events)",
                                Permaload = true,
                                IncludeAdjacents = true,
                                Enabled = true
                            }
                        }
                    }
                });

                Assert.IsNull(DatManager.CellDat,
                    "test bug: CellDat should be null when no .dat files were loaded, so this " +
                    "is not the scenario the guard exists for");

                // No exception is the assertion.
                LandblockManager.PreloadConfigLandblocks();
            }
            finally
            {
                ConfigManager.Initialize(saved);
            }
        }
    }
}
