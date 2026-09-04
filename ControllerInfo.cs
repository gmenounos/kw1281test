using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Text;

namespace BitFab.KW1281Test
{
    /// <summary>
    /// The info returned when a controller wakes up.
    /// </summary>
    internal class ControllerInfo
    {
        public ControllerInfo(IEnumerable<Block> blocks)
        {
            var sb = new StringBuilder();
            var asciiSb = new StringBuilder();
            foreach (var block in blocks)
            {
                if (block is AsciiDataBlock asciiBlock)
                {
                    sb.Append(asciiBlock);
                    asciiSb.Append(asciiBlock);
                    if (asciiBlock.MoreDataAvailable)
                    {
                        MoreDataAvailable = true;
                    }
                }
                else if (block is CodingWscBlock codingBlock)
                {
                    sb.Append($"{Environment.NewLine}{codingBlock}");
                    SoftwareCoding = codingBlock.SoftwareCoding;
                    WorkshopCode = codingBlock.WorkshopCode;
                }
                else
                {
                    Log.WriteLine($"Controller wakeup returned block of type {block.GetType()}");
                }
            }
            Text = sb.ToString();
            AsciiText = asciiSb.ToString();
        }

        public string Text { get; }

        /// <summary>
        /// Just the ASCII identification blocks (e.g. part number/description) — unlike
        /// <see cref="Text"/>, this excludes the rendered "Software Coding X, Workshop Code: Y"
        /// line, so it can serve as a stable key for measuring-block label-file matching.
        /// <see cref="Text"/> is the full identification, used as-is by the console output.
        /// </summary>
        public string AsciiText { get; }

        public bool MoreDataAvailable { get; }

        public int SoftwareCoding { get; }

        public int WorkshopCode { get; }

        public override string ToString()
        {
            return Text;
        }
    }
}
