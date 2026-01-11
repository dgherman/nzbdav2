using System;
using System.IO;
using System.Linq;

class Program {
    static void Main() {
        byte[] local = new byte[16];
        using (var fs = File.OpenRead("/mnt/downloads_local/completed/A.Thousand.Blows.S02E03.HDR.2160p.WEB.h265-ETHEL/A.Thousand.Blows.S02E03.HDR.2160p.WEB.h265-ETHEL.mkv")) {
            fs.Read(local, 0, 16);
        }
        
        byte[] virtualFile = new byte[16];
        using (var fs = File.OpenRead("/mnt/remote/nzbdav/content/sonarr4k/A.Thousand.Blows.S02E03.HDR.2160p.WEB.h265-ETHEL/A.Thousand.Blows.S02E03.HDR.2160p.WEB.h265-ETHEL.mkv")) {
            fs.Read(virtualFile, 0, 16);
        }
        
        Console.WriteLine("Local:   " + BitConverter.ToString(local).Replace("-", " "));
        Console.WriteLine("Virtual: " + BitConverter.ToString(virtualFile).Replace("-", " "));
        
        byte[] xor = new byte[16];
        for (int i = 0; i < 16; i++) xor[i] = (byte)(local[i] ^ virtualFile[i]);
        Console.WriteLine("XOR Key: " + BitConverter.ToString(xor).Replace("-", " "));
    }
}
