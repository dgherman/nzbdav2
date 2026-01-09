using System.Text;

namespace NzbWebDAV.Tools;

public static class MockNzbGenerator
{
    public static async Task GenerateAsync(string path, long totalSize, int segmentSize)
    {
        var sb = new StringBuilder();
        sb.AppendLine("<?xml version='1.0' encoding='utf-8' ?>");
        sb.AppendLine("<!DOCTYPE nzb PUBLIC '-//newzBin//DTD NZB 1.1//EN' 'http://www.newzbin.com/DTD/nzb/nzb-1.1.dtd'>");
        sb.AppendLine("<nzb xmlns='http://www.newzbin.com/DTD/2003/nzb'>");
        
        var segments = (int)Math.Ceiling((double)totalSize / segmentSize);
        var subject = "Mock_File_1GB.bin"; 
        var date = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        
        sb.Append($" <file poster='Mock' date='{date}' subject='{subject}'>\n");
        sb.AppendLine("  <groups><group>mock.group</group></groups>");
        sb.AppendLine("  <segments>");
        
        for (int i = 0; i < segments; i++)
        {
            var msgId = $"{Guid.NewGuid()}@mock.server";
            sb.Append($"   <segment bytes='{segmentSize}' number='{i + 1}'>{msgId}</segment>\n");
        }
        
        sb.AppendLine("  </segments>");
        sb.AppendLine(" </file>");
        sb.AppendLine("</nzb>");
        
        await File.WriteAllTextAsync(path, sb.ToString());
    }
}
