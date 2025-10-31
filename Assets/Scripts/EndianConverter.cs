using System;
using UnityEngine;

// Endian変換に関連する静的メソッドを集めたクラス
public static class EndianConverter
{
    public static double ReadDoubleBE(byte[] buf, int off)
    {
        // doubleは8バイト
        const int size = 8;
        
        // ターゲット環境がリトルエンディアンであることを前提とする
        if (BitConverter.IsLittleEndian)
        {
            // Big-Endian -> Little-Endianへバイト順序を反転させる
            byte[] tmp = new byte[size];
            for (int i = 0; i < size; i++)
            {
                // 元のバッファの i バイト目は、反転後のバッファの size - 1 - i バイト目になる
                tmp[i] = buf[off + (size - 1 - i)]; 
            }
            return BitConverter.ToDouble(tmp, 0);
        }
        else
        {
            // ターゲット環境がすでにBig-Endianの場合はそのまま読み込む (稀なケース)
            return BitConverter.ToDouble(buf, off);
        }
    }

    public static float ReadFloatBE(byte[] buf, int off)
    {
        const int size = 4;
        if (BitConverter.IsLittleEndian)
        {
            byte[] tmp = new byte[size];
            for (int i = 0; i < size; i++)
            {
                tmp[i] = buf[off + (size - 1 - i)];
            }
            return BitConverter.ToSingle(tmp, 0);
        }
        else
        {
            return BitConverter.ToSingle(buf, off);
        }
    }
    
    public static int ReadInt32BE(byte[] buf, int off)
    {
        const int size = 4;
        if (BitConverter.IsLittleEndian)
        {
            byte[] tmp = new byte[size];
            for (int i = 0; i < size; i++)
            {
                tmp[i] = buf[off + (size - 1 - i)];
            }
            return BitConverter.ToInt32(tmp, 0);
        }
        else
        {
            return BitConverter.ToInt32(buf, off);
        }
    }

    public static uint ReadUInt32BE(byte[] buf, int off)
    {
        const int size = 4;
        if (BitConverter.IsLittleEndian)
        {
            byte[] tmp = new byte[size];
            for (int i = 0; i < size; i++)
            {
                tmp[i] = buf[off + (size - 1 - i)];
            }
            return BitConverter.ToUInt32(tmp, 0);
        }
        else
        {
            return BitConverter.ToUInt32(buf, off);
        }
    }
}