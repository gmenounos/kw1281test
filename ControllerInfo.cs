using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Text;

namespace BitFab.KW1281Test;

/// <summary>
/// The info returned when a controller wakes up.
/// </summary>
internal class ControllerInfo
{
    public ControllerInfo(IEnumerable<Block> blocks)
    {
        var sb = new StringBuilder();
        foreach (var block in blocks)
        {
            if (block is AsciiDataBlock asciiBlock)
            {
                sb.Append(asciiBlock);
            }
            else if (block is CodingWscBlock codingBlock)
            {
                sb.Append($"{Environment.NewLine}{codingBlock}{Environment.NewLine}");
                SoftwareCoding = codingBlock.SoftwareCoding;
                WorkshopCode = codingBlock.WorkshopCode;
            }
            else
            {
                Log.WriteLine($"Controller wakeup returned block of type {block.GetType()}");
            }
        }
        Text = sb.ToString();
    }

    public string Text { get; }

    public int SoftwareCoding { get; }

    public int WorkshopCode { get; }

    public override string ToString()
    {
        return Text;
    }
}
