using System.Collections.Generic;

namespace BitFab.KW1281Test;

public record struct CommandArguments(uint Address,
                                      uint Length,
                                      byte Value,
                                      int SoftwareCoding,
                                      int WorkshopCode,
                                      List<KeyValuePair<ushort, byte>> AddressValuePairs,
                                      byte Channel,
                                      ushort ChannelValue,
                                      ushort? Login,
                                      byte GroupNumber,
                                      string? Filename);
