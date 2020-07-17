using BitFab.KW1281Test.Blocks;
using System;
using System.Collections.Generic;
using System.Text;

namespace BitFab.KW1281Test
{
    /// <summary>
    /// The info returned when a module wakes up.
    /// </summary>
    internal class ModuleInfo
    {
        public ModuleInfo(IEnumerable<Block> blocks)
        {
            var sb = new StringBuilder();
            foreach(var block in blocks)
            {
                if (block is AsciiDataBlock asciiBlock)
                {
                    sb.Append(asciiBlock);
                    if (asciiBlock.MoreDataAvailable)
                    {
                        MoreDataAvailable = true;
                    }
                }
                else
                {
                    Console.WriteLine($"Module wakeup returned block of type {block.GetType()}");
                }
            }
            Text = sb.ToString();
        }

        public string Text { get; }

        public bool MoreDataAvailable { get; }

        public override string ToString()
        {
            return Text;
        }
    }
}
