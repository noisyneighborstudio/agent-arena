// Pez palette (sRGB). Generated.
using UnityEngine;

namespace Pez
{
    public static class PezPalette
    {
        public static readonly Color32 TeamBlue = new Color32(46, 115, 255, 255);
        public static readonly Color32 TeamRed = new Color32(242, 46, 31, 255);
        public static readonly Color32 TeamGreen = new Color32(51, 217, 77, 255);
        public static readonly Color32 TeamYellow = new Color32(255, 209, 38, 255);
        public static readonly Color32 MaterialsCreamPlastic = new Color32(236, 228, 210, 255);
        public static readonly Color32 MaterialsSmokePlastic = new Color32(74, 79, 87, 255);
        public static readonly Color32 MaterialsSpringSteel = new Color32(142, 151, 159, 255);
        public static readonly Color32 MaterialsFoil = new Color32(200, 205, 211, 255);
        public static readonly Color32 MaterialsKraft = new Color32(169, 132, 90, 255);
        public static readonly Color32 MaterialsLicorice = new Color32(30, 27, 29, 255);
        public static readonly Color32 MaterialsSugarPad = new Color32(185, 174, 152, 255);
        public static readonly Color32 MaterialsBone = new Color32(246, 242, 232, 255);
        public static readonly Color32 MaterialsGrapeVent = new Color32(58, 36, 64, 255);
        public static readonly Color32 EmissiveCyanLaserOptics = new Color32(63, 230, 255, 255);
        public static readonly Color32 EmissiveMagentaPlasmaFusion = new Color32(255, 62, 200, 255);
        public static readonly Color32 EmissiveAcidUraniumElectronics = new Color32(182, 255, 59, 255);
        public static readonly Color32 EmissiveAmberIndustryDocking = new Color32(255, 162, 46, 255);
        public static readonly Color32 OreIronOre = new Color32(138, 58, 36, 255);
        public static readonly Color32 OreCopperOre = new Color32(200, 116, 47, 255);
        public static readonly Color32 OreCopperPatina = new Color32(111, 194, 166, 255);
        public static readonly Color32 OreCrystal = new Color32(143, 228, 255, 255);
        public static readonly Color32 OreUranium = new Color32(182, 255, 59, 255);
        public static readonly Color32 TerrainBiscuitGround = new Color32(125, 110, 85, 255);
        public static readonly Color32 TerrainBiscuitLight = new Color32(142, 127, 99, 255);
        public static readonly Color32 TerrainBiscuitDark = new Color32(106, 92, 70, 255);
        public static readonly Color32 TerrainLicoriceCliffTop = new Color32(74, 63, 64, 255);
        public static readonly Color32 TerrainLicoriceCliffFace = new Color32(36, 30, 32, 255);
        public static readonly Color32 TerrainColaWater = new Color32(58, 34, 24, 255);
        public static readonly Color32 TerrainShore = new Color32(160, 138, 104, 255);
        public static readonly Color32 TerrainCottonCandyTree = new Color32(138, 115, 133, 255);

        public static Color32 Team(int index)
        {
            switch (index) { case 0: return TeamBlue; case 1: return TeamRed; case 2: return TeamGreen; default: return TeamYellow; }
        }
    }
}
