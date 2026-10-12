using System;
namespace HVX.OWTS.Task38
{
    public static class PicoResolutionInfo
    {
        public static int DriverMode(int bits)
        {
            if (bits == 14) return 2;
            if (bits == 15) return 3;
            throw new ArgumentException("125 MS/s 采集仅提供14或15 bit模式；16 bit最高62.5 MS/s。");
        }
        public static int HardwareBits(int bits, int rangeIndex)
        {
            DriverMode(bits);
            if (rangeIndex < 0 || rangeIndex > 10) throw new ArgumentOutOfRangeException("rangeIndex");
            return bits - (rangeIndex == 0 ? 2 : rangeIndex == 1 ? 1 : 0);
        }
        public static string Describe(int bits, int rangeIndex)
        {
            return bits + " bit模式；当前量程规格 " + HardwareBits(bits, rangeIndex) + " bit\n规格位数不是实测有效位数 ENOB。";
        }
    }
}
