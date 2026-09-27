using System;
using System.Collections.Generic;

using ACE.DatLoader.Entity;
using ACE.DatLoader.FileTypes;

namespace ACE.DatLoader
{
    /// <summary>
    /// Builds stand-ins for the four client .dat databases, so the server can run with no .dat
    /// files present at all. Enabled by <c>Server.StartWithoutDats</c>.
    /// <para />
    /// The approach is: do not reimplement any of the parsers, and do not try to reproduce the
    /// retail data. Almost every dat call site in the server is satisfied by a default-constructed
    /// record, because <see cref="DatDatabase.ReadFromDat{T}"/> already returns one on a miss.
    /// Only three things need real values, and each is generated here:
    /// <list type="number">
    /// <item><description>cell geometry, or a landblock cannot be built at all,</description></item>
    /// <item><description>the region description the geometry builder walks,</description></item>
    /// <item><description>the XP curves, whose consumers index into them and would read empty.</description></item>
    /// </list>
    /// </summary>
    public static class SynthesizedDatabases
    {
        /// <summary>End-of-retail iteration numbers, reported so log lines stay readable.</summary>
        public const int IterationCell = 982;
        public const int IterationPortal = 2072;
        public const int IterationLanguage = 994;

        // ---- Cell geometry -------------------------------------------------------------------

        /// <summary>A landblock is an 8x8 grid of cells, i.e. a 9x9 grid of vertices.</summary>
        private const int CellLandblockSide = 9;

        /// <summary>
        /// The height byte every synthesized landblock uses. A single value makes the whole world
        /// perfectly flat, which is the point: there is no terrain to walk on, only an object graph
        /// to talk about.
        /// </summary>
        public const byte FlatHeight = 20;

        /// <summary>Terrain type 0 is grass; 0 in the low bits means "not a road".</summary>
        private const ushort FlatTerrain = 0;

        public static CellLandblock CreateFlatLandblock(uint id)
        {
            var cell = new CellLandblock
            {
                Id = id
            };

            for (var i = 0; i < CellLandblockSide * CellLandblockSide; i++)
            {
                cell.Terrain.Add(FlatTerrain);
                cell.Height.Add(FlatHeight);
            }

            return cell;
        }

        // ---- Region description ---------------------------------------------------------------

        /// <summary>
        /// The land height table maps a height byte to a Z. 256 entries, matching retail.
        /// </summary>
        private const int LandHeightTableSize = 256;

        /// <summary>Dereth's usable band is roughly -100 to +400; a linear ramp covers it.</summary>
        private const float LandHeightMin = -100.0f;
        private const float LandHeightRange = 500.0f;

        /// <summary>Terrain type count in the retail RegionDesc.</summary>
        private const int TerrainTypeCount = 32;

        /// <summary>Scene types addressable per terrain type. Retail carries 32.</summary>
        private const int SceneTypesPerTerrain = 32;

        /// <summary>Total scene types in the retail SceneDesc.</summary>
        private const int SceneTypeCount = 89;

        /// <summary>
        /// The minimum a landblock needs: the height table it indexes per-vertex, and the
        /// terrain/scene maps the geometry builder walks. Every scene list is left empty, which is
        /// the "no scenery anywhere" case, and every scene id is 0.
        /// <para />
        /// The height table is what <c>LandDefs</c>'s static constructor reads, and
        /// <c>LandHeightTable[Height[vertex]]</c> is indexed for all 81 vertices, so an empty or
        /// short table throws out of the landblock constructor.
        /// </summary>
        public static RegionDesc CreateRegionDesc()
        {
            var regionDesc = new RegionDesc
            {
                Id = RegionDesc.FILE_ID
            };

            var landHeightTable = regionDesc.LandDefs.LandHeightTable;
            for (var i = 0; i < LandHeightTableSize; i++)
                landHeightTable.Add(LandHeightMin + LandHeightRange * i / (LandHeightTableSize - 1));

            for (var t = 0; t < TerrainTypeCount; t++)
            {
                var terrainType = new TerrainType();

                for (var s = 0; s < SceneTypesPerTerrain; s++)
                    terrainType.SceneTypes.Add(0);

                regionDesc.TerrainInfo.TerrainTypes.Add(terrainType);
            }

            for (var s = 0; s < SceneTypeCount; s++)
                regionDesc.SceneInfo.SceneTypes.Add(new SceneType());

            return regionDesc;
        }

        // ---- XP tables ------------------------------------------------------------------------

        /// <summary>
        /// Retail max level. <c>Player_Xp.GetMaxLevel()</c> is <c>CharacterLevelXPList.Count - 1</c>,
        /// so the list needs 276 entries to resolve to 275.
        /// </summary>
        public const int MaxCharacterLevel = 275;

        /// <summary>Total XP to reach max level in the retail table.</summary>
        private const ulong CharacterLevelTotalXp = 191226310247;

        /// <summary>
        /// Anchor used to shape the level curve. The retail curve is S-shaped, which no single power
        /// law or exponential reproduces: a plain exponential anchored at both ends is off by 13x at
        /// max level, and an unshifted power law collapses the first ten levels to nearly zero. A
        /// power law shifted by one rank tracks retail to within roughly 3x across the whole range
        /// while hitting both anchors exactly.
        /// </summary>
        private const int CharacterLevelAnchorIndex = 2;
        private const ulong CharacterLevelAnchorXp = 1000;

        /// <summary>Retail max ranks, and the XP totals that close each table.</summary>
        public const int MaxAttributeRank = 190;
        public const int MaxVitalRank = 196;
        public const int MaxTrainedSkillRank = 208;
        public const int MaxSpecializedSkillRank = 226;

        private const uint AttributeTotalXp = 4019438644;
        private const uint VitalTotalXp = 4285430197;
        private const uint TrainedSkillTotalXp = 4203819496;
        private const uint SpecializedSkillTotalXp = 4100490438;

        /// <summary>
        /// First entries of the retail rank tables, used as the shaping anchor. Index 1 of the
        /// character level table is 0, which is why the level curve anchors on index 2 instead.
        /// </summary>
        private const uint AttributeAnchorXp = 110;
        private const uint VitalAnchorXp = 73;
        private const uint TrainedSkillAnchorXp = 58;
        private const uint SpecializedSkillAnchorXp = 23;

        private const int RankAnchorIndex = 1;

        /// <summary>
        /// The 46 levels at which retail grants a skill credit. Hardcoded because the totals are
        /// load-bearing: <c>Player_Xp.CheckForLevelup</c> reads this list by level index and would
        /// hand out nothing at all without it.
        /// </summary>
        private static readonly int[] SkillCreditLevels =
        {
            2, 3, 4, 5, 6, 7, 8, 9, 10, 12, 14, 16, 18, 20, 23, 26, 29, 32, 35, 40, 45, 50,
            55, 60, 65, 70, 75, 80, 85, 90, 95, 100, 105, 110, 115, 120, 125, 130, 140, 150,
            160, 180, 200, 225, 250, 275
        };

        /// <summary>
        /// Builds an XP table with the retail entry counts, so max level resolves to 275 and max rank
        /// resolves to the retail caps, and monotonic curves, so the level-up loop terminates and the
        /// rank lookups do not read off the end of a list.
        /// <para />
        /// The totals are pinned to the retail values, and that is the part that matters beyond
        /// shape: <c>CreatureAttribute.ExperienceLeft</c> and <c>CreatureVital.ExperienceLeft</c>
        /// compute <c>table[Count - 1] - ExperienceSpent</c> in unsigned arithmetic, so a synthesized
        /// total lower than a weenie's real accumulated XP would wrap around to a huge number.
        /// <para />
        /// The intermediate values are synthetic and will not match retail. They sit within about 3x
        /// across the range, which is irrelevant to protocol work and wrong for gameplay.
        /// </summary>
        public static XpTable CreateXpTable()
        {
            var xpTable = new XpTable
            {
                Id = XpTable.FILE_ID
            };

            var levelCurve = CreateLevelCurve(MaxCharacterLevel, CharacterLevelTotalXp,
                                              CharacterLevelAnchorIndex, CharacterLevelAnchorXp);

            // Retail has level 1 at 0 XP. The level-up loop starts at Level + 1 so it never reads
            // this index, but GetRemainingXP(1) does, and a flat 0 there matches a real shard.
            levelCurve[1] = 0;

            for (var i = 0; i < levelCurve.Count; i++)
            {
                xpTable.CharacterLevelXPList.Add(levelCurve[i]);

                var credits = Array.IndexOf(SkillCreditLevels, i) != -1 ? 1u : 0u;
                xpTable.CharacterLevelSkillCreditList.Add(credits);
            }

            AddRankCurve(xpTable.AttributeXpList, MaxAttributeRank, AttributeTotalXp, AttributeAnchorXp);
            AddRankCurve(xpTable.VitalXpList, MaxVitalRank, VitalTotalXp, VitalAnchorXp);
            AddRankCurve(xpTable.TrainedSkillXpList, MaxTrainedSkillRank, TrainedSkillTotalXp, TrainedSkillAnchorXp);
            AddRankCurve(xpTable.SpecializedSkillXpList, MaxSpecializedSkillRank, SpecializedSkillTotalXp, SpecializedSkillAnchorXp);

            return xpTable;
        }

        private static void AddRankCurve(List<uint> target, int maxRank, uint totalXp, uint anchorXp)
        {
            foreach (var xp in CreateLevelCurve(maxRank, totalXp, RankAnchorIndex, anchorXp))
                target.Add((uint)xp);
        }

        /// <summary>
        /// Generates <c>maxRank + 1</c> monotonically increasing cumulative values, hitting
        /// <paramref name="totalXp"/> at <paramref name="maxRank"/> and <paramref name="anchorXp"/> at
        /// <paramref name="anchorIndex"/>.
        /// <para />
        /// Shape: a power law shifted by one rank,
        /// <c>xp(i) = total * ((i + 1) / (maxRank + 1)) ^ p</c>, with <c>p</c> solved so the curve
        /// passes through the anchor. Index 0 is always 0.
        /// </summary>
        private static List<ulong> CreateLevelCurve(int maxRank, ulong totalXp, int anchorIndex, ulong anchorXp)
        {
            var curve = new List<ulong>(maxRank + 1);

            var p = Math.Log((double)anchorXp / totalXp) / Math.Log((anchorIndex + 1.0) / (maxRank + 1.0));

            for (var i = 0; i <= maxRank; i++)
            {
                if (i == 0)
                {
                    curve.Add(0);
                    continue;
                }

                var t = (i + 1.0) / (maxRank + 1.0);
                curve.Add((ulong)Math.Round(totalXp * Math.Pow(t, p)));
            }

            return curve;
        }
    }

    /// <summary>
    /// A cell database that answers every cell id with a synthesized flat landblock.
    /// </summary>
    internal sealed class SynthesizedCellDatDatabase : CellDatDatabase
    {
        public SynthesizedCellDatDatabase() : base(true) { }

        public override int Iteration => SynthesizedDatabases.IterationCell;

        public override T ReadFromDat<T>(uint fileId)
        {
            if (typeof(T) == typeof(CellLandblock))
                return (T)(object)SynthesizedDatabases.CreateFlatLandblock(fileId);

            return base.ReadFromDat<T>(fileId);
        }
    }

    internal sealed class SynthesizedPortalDatDatabase : PortalDatDatabase
    {
        public SynthesizedPortalDatDatabase() : base(true) { }

        public override int Iteration => SynthesizedDatabases.IterationPortal;
    }

    internal sealed class SynthesizedLanguageDatDatabase : LanguageDatDatabase
    {
        public SynthesizedLanguageDatDatabase() : base(true) { }

        public override int Iteration => SynthesizedDatabases.IterationLanguage;
    }
}
