﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace BitFab.KW1281Test.Blocks
{
    internal class GroupReadResponseWithTextBlock : Block
    {
        public GroupReadResponseWithTextBlock(List<byte> bytes)
            : base(bytes)
        {
            var bodyBytes = new List<byte>(Body);
            while (bodyBytes.Count > 2)
            {
                var subBlockHeader = bodyBytes.Take(3).ToArray();
                bodyBytes = bodyBytes.Skip(3).ToList();

                int subBlockBodyLength = subBlockHeader[2];
                if (bodyBytes.Count < subBlockBodyLength)
                {
                    throw new InvalidOperationException(
                        $"{nameof(GroupReadResponseWithTextBlock)} body ({Utils.Dump(Body, true)}) contains extra bytes after sub-blocks.");
                }

                var subBlock = new SubBlock
                {
                    BlockType = subBlockHeader[0],
                    Data = subBlockHeader[1],
                    Body = bodyBytes.Take(subBlockBodyLength).ToArray()
                };
                bodyBytes = bodyBytes.Skip(subBlockBodyLength).ToList();

                SubBlocks.Add(subBlock);
                
                if (subBlock.BlockType == 0x8D)
                {
                    var text = Encoding.ASCII.GetString(subBlock.Body, 0, subBlock.Body.Length);
                    _text = text.Split((char)0x03).ToList();
                }
            }

            if (bodyBytes.Count > 0)
            {
                throw new InvalidOperationException(
                    $"{nameof(GroupReadResponseWithTextBlock)} body ({Utils.Dump(Body, true)}) contains extra bytes after sub-blocks.");
            }
        }

        private readonly List<string> _text = new();

        public string GetText(int i)
        {
            if (i >= 0 && i < _text.Count)
            {
                return $"\"{_text[i]}\"";
            }

            return i.ToString();
        }

        public override string ToString()
        {
            var sb = new StringBuilder();

            foreach (var subBlock in SubBlocks)
            {
                sb.Append(subBlock.ToString());
            }

            return sb.ToString();
        }

        readonly List<SubBlock> SubBlocks = new();

        class SubBlock
        {
            public byte BlockType { get; init; }

            public byte Data { get; init; }

            public byte[] Body { get; init; } = Array.Empty<byte>();

            public override string ToString()
            {
                switch(BlockType)
                {
                    case 0x8D:
                        return $"(${BlockType:X2} ${Data:X2} {Encoding.ASCII.GetString(Body, 0, Body.Length).Replace((char)0x03, '|')})";

                    default:
                        return $"(${BlockType:X2} ${Data:X2}{Utils.Dump(Body)})";
                }
            }
        }
    }
}
