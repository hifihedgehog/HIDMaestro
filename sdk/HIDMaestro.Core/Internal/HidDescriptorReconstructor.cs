using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace HIDMaestro.Internal;

/// <summary>
/// Reconstruct a HID report descriptor from a Windows preparsed-data blob
/// (the opaque pointer returned by <c>HidD_GetPreparsedData</c>).
///
/// <para>The algorithm is a C# port of libusb/hidapi's
/// <c>hid_winapi_descriptor_reconstruct_pp_data</c> (see
/// <c>windows/hidapi_descriptor_reconstruct.c</c>), which was itself
/// derived from Chromium's WebHID reverse-engineering of Microsoft's
/// undocumented preparsed-data layout. Upstream is BSD/MIT-style
/// licensed; this port preserves the algorithm faithfully and keeps
/// attribution.</para>
///
/// <para>The reconstructed descriptor is <b>logically equivalent</b> to
/// what the device originally returned over USB (same report IDs, field
/// layouts, logical ranges, usage pages, sizes) but not byte-for-byte
/// identical. Filter drivers can mutate the descriptor before it reaches
/// user mode anyway, and some ordering/padding information is lost
/// during the HID parser's initial pass. For HIDMaestro's purpose —
/// creating a virtual controller that behaves identically to a physical
/// one — logical equivalence is the correct fidelity bar.</para>
/// </summary>
internal static class HidDescriptorReconstructor
{
    // HID short-item tags (USB HID spec 1.11 §6.2.2.2). Low two bits are
    // the data-size field (0, 1, 2, or 4 bytes) and are zero in these
    // constants; the writer adds them based on the actual encoded size.
    private const byte MainInput             = 0x80;
    private const byte MainOutput            = 0x90;
    private const byte MainFeature           = 0xB0;
    private const byte MainCollection        = 0xA0;
    private const byte MainCollectionEnd     = 0xC0;
    private const byte GlobalUsagePage       = 0x04;
    private const byte GlobalLogicalMinimum  = 0x14;
    private const byte GlobalLogicalMaximum  = 0x24;
    private const byte GlobalPhysicalMinimum = 0x34;
    private const byte GlobalPhysicalMaximum = 0x44;
    private const byte GlobalUnitExponent    = 0x54;
    private const byte GlobalUnit            = 0x64;
    private const byte GlobalReportSize      = 0x74;
    private const byte GlobalReportId        = 0x84;
    private const byte GlobalReportCount     = 0x94;
    private const byte LocalUsage            = 0x08;
    private const byte LocalUsageMinimum     = 0x18;
    private const byte LocalUsageMaximum     = 0x28;
    private const byte LocalDesignatorIndex  = 0x38;
    private const byte LocalDesignatorMinimum= 0x48;
    private const byte LocalDesignatorMaximum= 0x58;
    private const byte LocalString           = 0x78;
    private const byte LocalStringMinimum    = 0x88;
    private const byte LocalStringMaximum    = 0x98;
    private const byte LocalDelimiter        = 0xA8;

    // Internal main-item-type tags. Values 0/1/2 intentionally match
    // HidP_Input/Output/Feature so <c>MainItems</c> doubles as an array index.
    private enum MainItems
    {
        Input = 0,
        Output = 1,
        Feature = 2,
        Collection = 3,
        CollectionEnd = 4,
        DelimiterOpen = 5,
        DelimiterUsage = 6,
        DelimiterClose = 7,
    }

    private enum NodeType
    {
        Cap,
        Padding,
        Collection,
    }

    private sealed class ItemNode
    {
        public int FirstBit;
        public int LastBit;
        public NodeType TypeOfNode;
        public int CapsIndex;
        public int CollectionIndex;
        public MainItems MainItemType;
        public byte ReportId;
        public ItemNode? Next;
    }

    private sealed class Buffer
    {
        public readonly List<byte> Bytes = new(512);
    }

    /// <summary>Reconstruct the HID report descriptor from a preparsed-data
    /// pointer. Returns the descriptor bytes on success; throws
    /// <see cref="InvalidOperationException"/> if the preparsed-data
    /// magic is invalid.</summary>
    public static byte[] Reconstruct(IntPtr preparsedData)
    {
        if (preparsedData == IntPtr.Zero)
            throw new ArgumentNullException(nameof(preparsedData));

        // Verify magic "HidP KDR".
        byte[] magic = new byte[8];
        Marshal.Copy(preparsedData, magic, 0, 8);
        if (magic[0] != (byte)'H' || magic[1] != (byte)'i' || magic[2] != (byte)'d' || magic[3] != (byte)'P'
            || magic[4] != (byte)' ' || magic[5] != (byte)'K' || magic[6] != (byte)'D' || magic[7] != (byte)'R')
        {
            throw new InvalidOperationException(
                "Preparsed-data magic key mismatch; pointer does not appear to be a valid HIDP_PREPARSED_DATA.");
        }

        // Read the header fields we care about.
        ushort numLinkCollectionNodes = (ushort)Marshal.ReadInt16(preparsedData,
            HidPreparsedLayout.OffsetNumberLinkCollectionNodes);
        ushort firstByteOfLinkCollectionArray = (ushort)Marshal.ReadInt16(preparsedData,
            HidPreparsedLayout.OffsetFirstByteOfLinkCollectionArray);

        // caps_info[3] (24 bytes).
        var capsInfo = new HidPpCapsInfo[HidPreparsedLayout.NumReportTypes];
        for (int i = 0; i < HidPreparsedLayout.NumReportTypes; i++)
        {
            IntPtr p = IntPtr.Add(preparsedData, HidPreparsedLayout.OffsetCapsInfoStart + i * 8);
            capsInfo[i] = Marshal.PtrToStructure<HidPpCapsInfo>(p);
        }

        // Determine how many cap entries we need to read. The caps array
        // in the preparsed blob is shared by Input/Output/Feature; the
        // highest LastCap across the three tells us the total.
        int totalCaps = 0;
        for (int i = 0; i < HidPreparsedLayout.NumReportTypes; i++)
        {
            if (capsInfo[i].LastCap > totalCaps) totalCaps = capsInfo[i].LastCap;
        }

        // Read the caps array.
        var caps = new HidPpCap[totalCaps];
        for (int i = 0; i < totalCaps; i++)
        {
            IntPtr p = IntPtr.Add(preparsedData, HidPreparsedLayout.OffsetCapsArrayStart + i * HidPreparsedLayout.CapSize);
            caps[i] = Marshal.PtrToStructure<HidPpCap>(p);
        }

        // Read the link collection array. Its base is
        // (&caps[0] + FirstByteOfLinkCollectionArray), measured from the
        // start of the caps region (= preparsed + 44).
        var linkCollections = new HidPpLinkCollectionNode[numLinkCollectionNodes];
        IntPtr lcBase = IntPtr.Add(preparsedData, HidPreparsedLayout.OffsetCapsArrayStart + firstByteOfLinkCollectionArray);
        for (int i = 0; i < numLinkCollectionNodes; i++)
        {
            IntPtr p = IntPtr.Add(lcBase, i * HidPreparsedLayout.LinkCollectionNodeSize);
            linkCollections[i] = Marshal.PtrToStructure<HidPpLinkCollectionNode>(p);
        }

        // From here on, the algorithm is a straight transliteration of the
        // upstream C code. Names preserved for cross-reference.

        // Step 1: coll_bit_range[coll][reportId][rt] lookup table.
        // Represented as a flat 3D [coll, 256, NumReportTypes] array of (first, last) ints.
        int totalCollIdx = numLinkCollectionNodes;
        int collBitRangeSize = totalCollIdx * 256 * HidPreparsedLayout.NumReportTypes;
        var collBitRangeFirst = new int[collBitRangeSize];
        var collBitRangeLast  = new int[collBitRangeSize];
        for (int i = 0; i < collBitRangeSize; i++) { collBitRangeFirst[i] = -1; collBitRangeLast[i] = -1; }
        int CB(int coll, int rid, int rt) => (coll * 256 + rid) * HidPreparsedLayout.NumReportTypes + rt;

        // Fill table where caps exist.
        for (int rt = 0; rt < HidPreparsedLayout.NumReportTypes; rt++)
        {
            for (int capIdx = capsInfo[rt].FirstCap; capIdx < capsInfo[rt].LastCap; capIdx++)
            {
                ref var c = ref caps[capIdx];
                int firstBit = (c.BytePosition - 1) * 8 + c.BitPosition;
                int lastBit = firstBit + c.ReportSize * c.ReportCount - 1;
                int idx = CB(c.LinkCollection, c.ReportID, rt);
                if (collBitRangeFirst[idx] == -1 || collBitRangeFirst[idx] > firstBit)
                    collBitRangeFirst[idx] = firstBit;
                if (collBitRangeLast[idx] < lastBit)
                    collBitRangeLast[idx] = lastBit;
            }
        }

        // Step 2: collection hierarchy level + direct-child counts.
        int maxCollLevel = 0;
        int[] collLevels = new int[totalCollIdx];
        int[] collNumberOfDirectChilds = new int[totalCollIdx];
        for (int i = 0; i < totalCollIdx; i++) { collLevels[i] = -1; collNumberOfDirectChilds[i] = 0; }
        {
            int actualCollLevel = 0;
            ushort collectionNodeIdx = 0;
            while (actualCollLevel >= 0)
            {
                collLevels[collectionNodeIdx] = actualCollLevel;
                if (linkCollections[collectionNodeIdx].NumberOfChildren > 0 &&
                    collLevels[linkCollections[collectionNodeIdx].FirstChild] == -1)
                {
                    actualCollLevel++;
                    collLevels[collectionNodeIdx] = actualCollLevel;
                    if (maxCollLevel < actualCollLevel) maxCollLevel = actualCollLevel;
                    collNumberOfDirectChilds[collectionNodeIdx]++;
                    collectionNodeIdx = linkCollections[collectionNodeIdx].FirstChild;
                }
                else if (linkCollections[collectionNodeIdx].NextSibling != 0)
                {
                    collNumberOfDirectChilds[linkCollections[collectionNodeIdx].Parent]++;
                    collectionNodeIdx = linkCollections[collectionNodeIdx].NextSibling;
                }
                else
                {
                    actualCollLevel--;
                    if (actualCollLevel >= 0)
                        collectionNodeIdx = linkCollections[collectionNodeIdx].Parent;
                }
            }
        }

        // Step 3: propagate bit ranges from children to parents.
        for (int actualCollLevel = maxCollLevel - 1; actualCollLevel >= 0; actualCollLevel--)
        {
            for (ushort collectionNodeIdx = 0; collectionNodeIdx < totalCollIdx; collectionNodeIdx++)
            {
                if (collLevels[collectionNodeIdx] != actualCollLevel) continue;
                ushort childIdx = linkCollections[collectionNodeIdx].FirstChild;
                while (childIdx != 0)
                {
                    for (int rid = 0; rid < 256; rid++)
                    {
                        for (int rt = 0; rt < HidPreparsedLayout.NumReportTypes; rt++)
                        {
                            int childI = CB(childIdx, rid, rt);
                            int parentI = CB(collectionNodeIdx, rid, rt);
                            if (collBitRangeFirst[childI] != -1 &&
                                collBitRangeFirst[parentI] > collBitRangeFirst[childI])
                                collBitRangeFirst[parentI] = collBitRangeFirst[childI];
                            if (collBitRangeLast[parentI] < collBitRangeLast[childI])
                                collBitRangeLast[parentI] = collBitRangeLast[childI];
                            childIdx = linkCollections[childIdx].NextSibling;
                        }
                    }
                }
            }
        }

        // Step 4: sorted child collection order.
        ushort[]?[] collChildOrder = new ushort[totalCollIdx][];
        {
            bool[] collParsedFlag = new bool[totalCollIdx];
            int actualCollLevel = 0;
            ushort collectionNodeIdx = 0;
            while (actualCollLevel >= 0)
            {
                if (collNumberOfDirectChilds[collectionNodeIdx] != 0 &&
                    !collParsedFlag[linkCollections[collectionNodeIdx].FirstChild])
                {
                    collParsedFlag[linkCollections[collectionNodeIdx].FirstChild] = true;
                    var order = new ushort[collNumberOfDirectChilds[collectionNodeIdx]];
                    collChildOrder[collectionNodeIdx] = order;

                    // Build reverse order: sibling chain order is already
                    // the inverse of the original report order, so we
                    // reverse it to get the natural ordering.
                    {
                        ushort childIdx = linkCollections[collectionNodeIdx].FirstChild;
                        int childCount = collNumberOfDirectChilds[collectionNodeIdx] - 1;
                        order[childCount] = childIdx;
                        while (linkCollections[childIdx].NextSibling != 0)
                        {
                            childCount--;
                            childIdx = linkCollections[childIdx].NextSibling;
                            order[childCount] = childIdx;
                        }
                    }

                    // If multiple children, sort by first-bit position across all (reportid, rt) combos.
                    if (order.Length > 1)
                    {
                        for (int rt = 0; rt < HidPreparsedLayout.NumReportTypes; rt++)
                        {
                            for (int rid = 0; rid < 256; rid++)
                            {
                                for (int i = 1; i < order.Length; i++)
                                {
                                    int prev = order[i - 1];
                                    int cur = order[i];
                                    int pI = CB(prev, rid, rt);
                                    int cI = CB(cur, rid, rt);
                                    if (collBitRangeFirst[pI] != -1 &&
                                        collBitRangeFirst[cI] != -1 &&
                                        collBitRangeFirst[pI] > collBitRangeFirst[cI])
                                    {
                                        (order[i - 1], order[i]) = (order[i], order[i - 1]);
                                    }
                                }
                            }
                        }
                    }

                    actualCollLevel++;
                    collectionNodeIdx = linkCollections[collectionNodeIdx].FirstChild;
                }
                else if (linkCollections[collectionNodeIdx].NextSibling != 0)
                {
                    collectionNodeIdx = linkCollections[collectionNodeIdx].NextSibling;
                }
                else
                {
                    actualCollLevel--;
                    if (actualCollLevel >= 0)
                        collectionNodeIdx = linkCollections[collectionNodeIdx].Parent;
                }
            }
        }

        // Step 5: build main_item_list with Collection/CollectionEnd items.
        ItemNode? mainItemList = null;
        ItemNode?[] collBeginLookup = new ItemNode[totalCollIdx];
        ItemNode?[] collEndLookup = new ItemNode[totalCollIdx];
        {
            int[] collLastWrittenChild = new int[totalCollIdx];
            for (int i = 0; i < totalCollIdx; i++) collLastWrittenChild[i] = -1;

            int actualCollLevel = 0;
            ushort collectionNodeIdx = 0;
            ItemNode? firstDelimiterNode = null;
            ItemNode? delimiterCloseNode = null;
            collBeginLookup[0] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.Collection, 0, ref mainItemList);
            while (actualCollLevel >= 0)
            {
                if (collNumberOfDirectChilds[collectionNodeIdx] != 0 &&
                    collLastWrittenChild[collectionNodeIdx] == -1)
                {
                    collLastWrittenChild[collectionNodeIdx] = collChildOrder[collectionNodeIdx]![0];
                    collectionNodeIdx = collChildOrder[collectionNodeIdx]![0];

                    if (linkCollections[collectionNodeIdx].IsAlias && firstDelimiterNode == null)
                    {
                        firstDelimiterNode = mainItemList;
                        collBeginLookup[collectionNodeIdx] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.DelimiterUsage, 0, ref mainItemList);
                        collBeginLookup[collectionNodeIdx] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.DelimiterClose, 0, ref mainItemList);
                        delimiterCloseNode = mainItemList;
                    }
                    else
                    {
                        collBeginLookup[collectionNodeIdx] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.Collection, 0, ref mainItemList);
                        actualCollLevel++;
                    }
                }
                else if (collNumberOfDirectChilds[collectionNodeIdx] > 1 &&
                         collLastWrittenChild[collectionNodeIdx] != collChildOrder[collectionNodeIdx]![collNumberOfDirectChilds[collectionNodeIdx] - 1])
                {
                    int nextChild = 1;
                    while (collLastWrittenChild[collectionNodeIdx] != collChildOrder[collectionNodeIdx]![nextChild - 1])
                        nextChild++;
                    collLastWrittenChild[collectionNodeIdx] = collChildOrder[collectionNodeIdx]![nextChild];
                    collectionNodeIdx = collChildOrder[collectionNodeIdx]![nextChild];

                    if (linkCollections[collectionNodeIdx].IsAlias && firstDelimiterNode == null)
                    {
                        firstDelimiterNode = mainItemList;
                        collBeginLookup[collectionNodeIdx] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.DelimiterUsage, 0, ref mainItemList);
                        collBeginLookup[collectionNodeIdx] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.DelimiterClose, 0, ref mainItemList);
                        delimiterCloseNode = mainItemList;
                    }
                    else if (linkCollections[collectionNodeIdx].IsAlias && firstDelimiterNode != null)
                    {
                        var fdn = firstDelimiterNode;
                        collBeginLookup[collectionNodeIdx] = InsertAfter(fdn!, 0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.DelimiterUsage, 0);
                    }
                    else if (!linkCollections[collectionNodeIdx].IsAlias && firstDelimiterNode != null)
                    {
                        var fdn = firstDelimiterNode;
                        collBeginLookup[collectionNodeIdx] = InsertAfter(fdn!, 0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.DelimiterUsage, 0);
                        collBeginLookup[collectionNodeIdx] = InsertAfter(fdn!, 0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.DelimiterOpen, 0);
                        firstDelimiterNode = null;
                        mainItemList = delimiterCloseNode;
                        delimiterCloseNode = null;
                    }
                    if (!linkCollections[collectionNodeIdx].IsAlias)
                    {
                        collBeginLookup[collectionNodeIdx] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.Collection, 0, ref mainItemList);
                        actualCollLevel++;
                    }
                }
                else
                {
                    actualCollLevel--;
                    collEndLookup[collectionNodeIdx] = AppendNode(0, 0, NodeType.Collection, 0, collectionNodeIdx, MainItems.CollectionEnd, 0, ref mainItemList);
                    collectionNodeIdx = linkCollections[collectionNodeIdx].Parent;
                }
            }
        }

        // Step 6: insert Input/Output/Feature items in bit-position order.
        for (int rt = 0; rt < HidPreparsedLayout.NumReportTypes; rt++)
        {
            ItemNode? firstDelimiterNode = null;
            ItemNode? delimiterCloseNode = null;
            for (int capsIdx = capsInfo[rt].FirstCap; capsIdx < capsInfo[rt].LastCap; capsIdx++)
            {
                ref var c = ref caps[capsIdx];
                ItemNode? collBegin = collBeginLookup[c.LinkCollection];
                int firstBit = (c.BytePosition - 1) * 8 + c.BitPosition;
                int lastBit = firstBit + c.ReportSize * c.ReportCount - 1;

                for (int childIdx = 0; childIdx < collNumberOfDirectChilds[c.LinkCollection]; childIdx++)
                {
                    int childColl = collChildOrder[c.LinkCollection]![childIdx];
                    if (firstBit < collBitRangeFirst[CB(childColl, c.ReportID, rt)])
                        break;
                    collBegin = collEndLookup[childColl];
                }
                var listNode = SearchForBitPosition(collBegin!, firstBit, (MainItems)rt, c.ReportID);

                if (c.IsAlias && firstDelimiterNode == null)
                {
                    firstDelimiterNode = listNode;
                    InsertAfter(listNode, firstBit, lastBit, NodeType.Cap, capsIdx, c.LinkCollection, MainItems.DelimiterUsage, c.ReportID);
                    var closeNode = InsertAfter(listNode, firstBit, lastBit, NodeType.Cap, capsIdx, c.LinkCollection, MainItems.DelimiterClose, c.ReportID);
                    delimiterCloseNode = closeNode;
                }
                else if (c.IsAlias && firstDelimiterNode != null)
                {
                    InsertAfter(listNode, firstBit, lastBit, NodeType.Cap, capsIdx, c.LinkCollection, MainItems.DelimiterUsage, c.ReportID);
                }
                else if (!c.IsAlias && firstDelimiterNode != null)
                {
                    InsertAfter(listNode, firstBit, lastBit, NodeType.Cap, capsIdx, c.LinkCollection, MainItems.DelimiterUsage, c.ReportID);
                    InsertAfter(listNode, firstBit, lastBit, NodeType.Cap, capsIdx, c.LinkCollection, MainItems.DelimiterOpen, c.ReportID);
                    firstDelimiterNode = null;
                    listNode = delimiterCloseNode!;
                    delimiterCloseNode = null;
                }
                if (!c.IsAlias)
                {
                    InsertAfter(listNode, firstBit, lastBit, NodeType.Cap, capsIdx, c.LinkCollection, (MainItems)rt, c.ReportID);
                }
            }
        }

        // Step 7: insert padding for bit gaps + 8-bit end-of-report alignment.
        {
            var lastBitPosition = new int[HidPreparsedLayout.NumReportTypes, 256];
            var lastReportItemLookup = new ItemNode?[HidPreparsedLayout.NumReportTypes, 256];
            for (int rt = 0; rt < HidPreparsedLayout.NumReportTypes; rt++)
                for (int rid = 0; rid < 256; rid++)
                    lastBitPosition[rt, rid] = -1;

            bool deviceHasReportIds = false;
            var list = mainItemList;
            ItemNode? nodeBeforeTopLevelCollEnd = null;

            while (list != null && list.Next != null)
            {
                int mt = (int)list.MainItemType;
                if (mt >= (int)MainItems.Input && mt <= (int)MainItems.Feature)
                {
                    if (list.FirstBit != -1)
                    {
                        if (lastBitPosition[mt, list.ReportId] + 1 != list.FirstBit &&
                            lastReportItemLookup[mt, list.ReportId] != null &&
                            lastReportItemLookup[mt, list.ReportId]!.FirstBit != list.FirstBit)
                        {
                            var ln = SearchForBitPosition(lastReportItemLookup[mt, list.ReportId]!,
                                lastBitPosition[mt, list.ReportId], list.MainItemType, list.ReportId);
                            InsertAfter(ln,
                                lastBitPosition[mt, list.ReportId] + 1,
                                list.FirstBit - 1,
                                NodeType.Padding, -1, 0,
                                list.MainItemType, list.ReportId);
                        }
                        if (list.ReportId != 0) deviceHasReportIds = true;
                        lastBitPosition[mt, list.ReportId] = list.LastBit;
                        lastReportItemLookup[mt, list.ReportId] = list;
                    }
                }
                if (list.Next.MainItemType == MainItems.CollectionEnd)
                    nodeBeforeTopLevelCollEnd = list;
                list = list.Next;
            }

            // 8-bit padding at each report end.
            for (int rt = 0; rt < HidPreparsedLayout.NumReportTypes; rt++)
            {
                for (int rid = 0; rid < 256; rid++)
                {
                    if (lastBitPosition[rt, rid] != -1)
                    {
                        int padding = 8 - ((lastBitPosition[rt, rid] + 1) % 8);
                        if (padding < 8)
                        {
                            var lri = lastReportItemLookup[rt, rid]!;
                            var padNode = InsertAfter(lri,
                                lastBitPosition[rt, rid] + 1,
                                lastBitPosition[rt, rid] + padding,
                                NodeType.Padding, -1, 0,
                                (MainItems)rt, (byte)rid);
                            if (ReferenceEquals(lri, nodeBeforeTopLevelCollEnd))
                                nodeBeforeTopLevelCollEnd = padNode.Next;
                            lastBitPosition[rt, rid] += padding;
                        }
                    }
                }
            }

            // Full-byte trailing padding for no-Report-ID devices.
            for (int rt = 0; rt < HidPreparsedLayout.NumReportTypes; rt++)
            {
                if (!deviceHasReportIds && capsInfo[rt].NumberOfCaps > 0 && capsInfo[rt].ReportByteLength > 0)
                {
                    int padding = (capsInfo[rt].ReportByteLength - 1) * 8 - (lastBitPosition[rt, 0] + 1);
                    if (padding > 0 && nodeBeforeTopLevelCollEnd != null)
                    {
                        InsertAfter(nodeBeforeTopLevelCollEnd,
                            lastBitPosition[rt, 0] + 1,
                            lastBitPosition[rt, 0] + padding,
                            NodeType.Padding, -1, 0,
                            (MainItems)rt, 0);
                    }
                }
            }
        }

        // Step 8: encode to bytes.
        return Encode(mainItemList, linkCollections, caps);
    }

    // ── Linked-list helpers ───────────────────────────────────────

    private static ItemNode AppendNode(int firstBit, int lastBit, NodeType type, int capsIdx,
        int collIdx, MainItems mainItemType, byte reportId, ref ItemNode? list)
    {
        var node = new ItemNode
        {
            FirstBit = firstBit, LastBit = lastBit, TypeOfNode = type,
            CapsIndex = capsIdx, CollectionIndex = collIdx,
            MainItemType = mainItemType, ReportId = reportId,
        };
        if (list == null) { list = node; return node; }
        var cur = list;
        while (cur.Next != null) cur = cur.Next;
        cur.Next = node;
        return node;
    }

    private static ItemNode InsertAfter(ItemNode after, int firstBit, int lastBit, NodeType type,
        int capsIdx, int collIdx, MainItems mainItemType, byte reportId)
    {
        var node = new ItemNode
        {
            FirstBit = firstBit, LastBit = lastBit, TypeOfNode = type,
            CapsIndex = capsIdx, CollectionIndex = collIdx,
            MainItemType = mainItemType, ReportId = reportId,
            Next = after.Next,
        };
        after.Next = node;
        return node;
    }

    private static ItemNode SearchForBitPosition(ItemNode start, int searchBit, MainItems mainItemType, byte reportId)
    {
        var cur = start;
        while (cur.Next != null)
        {
            var n = cur.Next;
            if (n.MainItemType == MainItems.Collection || n.MainItemType == MainItems.CollectionEnd)
                break;
            if (n.LastBit >= searchBit && n.ReportId == reportId && n.MainItemType == mainItemType)
                break;
            cur = cur.Next;
        }
        return cur;
    }

    // ── HID short-item encoder ────────────────────────────────────

    private static void WriteShortItem(Buffer buf, byte rdItemTag, long data)
    {
        if ((rdItemTag & 0x03) != 0)
            throw new InvalidOperationException("rd_item low 2 bits must be zero; they encode size.");

        if (rdItemTag == MainCollectionEnd)
        {
            buf.Bytes.Add((byte)(rdItemTag + 0x00));
            return;
        }

        bool signed = rdItemTag == GlobalLogicalMinimum
                   || rdItemTag == GlobalLogicalMaximum
                   || rdItemTag == GlobalPhysicalMinimum
                   || rdItemTag == GlobalPhysicalMaximum;

        if (signed)
        {
            if (data >= -128 && data <= 127)
            {
                buf.Bytes.Add((byte)(rdItemTag + 0x01));
                buf.Bytes.Add((byte)(data & 0xFF));
            }
            else if (data >= -32768 && data <= 32767)
            {
                buf.Bytes.Add((byte)(rdItemTag + 0x02));
                buf.Bytes.Add((byte)(data & 0xFF));
                buf.Bytes.Add((byte)((data >> 8) & 0xFF));
            }
            else
            {
                buf.Bytes.Add((byte)(rdItemTag + 0x03));
                buf.Bytes.Add((byte)(data & 0xFF));
                buf.Bytes.Add((byte)((data >> 8) & 0xFF));
                buf.Bytes.Add((byte)((data >> 16) & 0xFF));
                buf.Bytes.Add((byte)((data >> 24) & 0xFF));
            }
        }
        else
        {
            if (data >= 0 && data <= 0xFF)
            {
                buf.Bytes.Add((byte)(rdItemTag + 0x01));
                buf.Bytes.Add((byte)(data & 0xFF));
            }
            else if (data >= 0 && data <= 0xFFFF)
            {
                buf.Bytes.Add((byte)(rdItemTag + 0x02));
                buf.Bytes.Add((byte)(data & 0xFF));
                buf.Bytes.Add((byte)((data >> 8) & 0xFF));
            }
            else
            {
                buf.Bytes.Add((byte)(rdItemTag + 0x03));
                buf.Bytes.Add((byte)(data & 0xFF));
                buf.Bytes.Add((byte)((data >> 8) & 0xFF));
                buf.Bytes.Add((byte)((data >> 16) & 0xFF));
                buf.Bytes.Add((byte)((data >> 24) & 0xFF));
            }
        }
    }

    // ── Main encoding pass ────────────────────────────────────────

    private static byte[] Encode(ItemNode? list, HidPpLinkCollectionNode[] linkCollections, HidPpCap[] caps)
    {
        var buf = new Buffer();

        byte lastReportId = 0;
        ushort lastUsagePage = 0;
        long lastPhysicalMin = 0;
        long lastPhysicalMax = 0;
        uint lastUnitExponent = 0;
        uint lastUnit = 0;
        bool inhibitWriteOfUsage = false;
        int reportCount = 0;

        while (list != null)
        {
            int rtIdx = (int)list.MainItemType;
            int capsIdx = list.CapsIndex;

            if (list.MainItemType == MainItems.Collection)
            {
                var lc = linkCollections[list.CollectionIndex];
                if (lastUsagePage != lc.LinkUsagePage)
                {
                    WriteShortItem(buf, GlobalUsagePage, lc.LinkUsagePage);
                    lastUsagePage = lc.LinkUsagePage;
                }
                if (inhibitWriteOfUsage)
                {
                    inhibitWriteOfUsage = false;
                }
                else
                {
                    WriteShortItem(buf, LocalUsage, lc.LinkUsage);
                }
                WriteShortItem(buf, MainCollection, lc.CollectionType);
            }
            else if (list.MainItemType == MainItems.CollectionEnd)
            {
                WriteShortItem(buf, MainCollectionEnd, 0);
            }
            else if (list.MainItemType == MainItems.DelimiterOpen)
            {
                if (list.CollectionIndex != -1)
                {
                    var lc = linkCollections[list.CollectionIndex];
                    if (lastUsagePage != lc.LinkUsagePage)
                    {
                        WriteShortItem(buf, GlobalUsagePage, lc.LinkUsagePage);
                        lastUsagePage = lc.LinkUsagePage;
                    }
                }
                else if (list.CapsIndex != 0)
                {
                    ref var c = ref caps[capsIdx];
                    if (c.UsagePage != lastUsagePage)
                    {
                        WriteShortItem(buf, GlobalUsagePage, c.UsagePage);
                        lastUsagePage = c.UsagePage;
                    }
                }
                WriteShortItem(buf, LocalDelimiter, 1);
            }
            else if (list.MainItemType == MainItems.DelimiterUsage)
            {
                if (list.CollectionIndex != -1)
                {
                    var lc = linkCollections[list.CollectionIndex];
                    WriteShortItem(buf, LocalUsage, lc.LinkUsage);
                }
                if (list.CapsIndex != 0)
                {
                    ref var c = ref caps[capsIdx];
                    if (c.IsRange)
                    {
                        WriteShortItem(buf, LocalUsageMinimum, c.UsageMin);
                        WriteShortItem(buf, LocalUsageMaximum, c.UsageMax);
                    }
                    else
                    {
                        WriteShortItem(buf, LocalUsage, c.UsageMin); // NotRange.Usage shares the UsageMin slot
                    }
                }
            }
            else if (list.MainItemType == MainItems.DelimiterClose)
            {
                WriteShortItem(buf, LocalDelimiter, 0);
                inhibitWriteOfUsage = true;
            }
            else if (list.TypeOfNode == NodeType.Padding)
            {
                int totalBits = list.LastBit - list.FirstBit + 1;
                if (totalBits % 8 == 0)
                {
                    WriteShortItem(buf, GlobalReportSize, 8);
                    WriteShortItem(buf, GlobalReportCount, totalBits / 8);
                }
                else
                {
                    WriteShortItem(buf, GlobalReportSize, totalBits);
                    WriteShortItem(buf, GlobalReportCount, 1);
                }
                byte mainTag = rtIdx switch
                {
                    HidPreparsedLayout.HidPInput   => MainInput,
                    HidPreparsedLayout.HidPOutput  => MainOutput,
                    HidPreparsedLayout.HidPFeature => MainFeature,
                    _ => MainInput,
                };
                WriteShortItem(buf, mainTag, 0x03);
                reportCount = 0;
            }
            else if (caps[capsIdx].IsButtonCap)
            {
                ref var c = ref caps[capsIdx];
                if (lastReportId != c.ReportID)
                {
                    WriteShortItem(buf, GlobalReportId, c.ReportID);
                    lastReportId = c.ReportID;
                }
                if (c.UsagePage != lastUsagePage)
                {
                    WriteShortItem(buf, GlobalUsagePage, c.UsagePage);
                    lastUsagePage = c.UsagePage;
                }

                if (c.IsRange)
                    reportCount += (c.DataIndexMax - c.DataIndexMin);

                if (inhibitWriteOfUsage)
                {
                    inhibitWriteOfUsage = false;
                }
                else
                {
                    if (c.IsRange)
                    {
                        WriteShortItem(buf, LocalUsageMinimum, c.UsageMin);
                        WriteShortItem(buf, LocalUsageMaximum, c.UsageMax);
                    }
                    else
                    {
                        WriteShortItem(buf, LocalUsage, c.UsageMin);
                    }
                }

                if (c.IsDesignatorRange)
                {
                    WriteShortItem(buf, LocalDesignatorMinimum, c.DesignatorMin);
                    WriteShortItem(buf, LocalDesignatorMaximum, c.DesignatorMax);
                }
                else if (c.DesignatorMin != 0) // NotRange.DesignatorIndex aliases DesignatorMin
                {
                    WriteShortItem(buf, LocalDesignatorIndex, c.DesignatorMin);
                }

                if (c.IsStringRange)
                {
                    WriteShortItem(buf, LocalStringMinimum, c.StringMin);
                    WriteShortItem(buf, LocalStringMaximum, c.StringMax);
                }
                else if (c.StringMin != 0) // NotRange.StringIndex aliases StringMin
                {
                    WriteShortItem(buf, LocalString, c.StringMin);
                }

                // Merge consecutive identical single-button usages into one Report Count.
                if (list.Next != null &&
                    (int)list.Next.MainItemType == rtIdx &&
                    list.Next.TypeOfNode == NodeType.Cap &&
                    caps[list.Next.CapsIndex].IsButtonCap &&
                    !c.IsRange &&
                    !caps[list.Next.CapsIndex].IsRange &&
                    caps[list.Next.CapsIndex].UsagePage == c.UsagePage &&
                    caps[list.Next.CapsIndex].ReportID == c.ReportID &&
                    caps[list.Next.CapsIndex].BitField == c.BitField)
                {
                    if (list.Next.FirstBit != list.FirstBit)
                        reportCount++;
                }
                else
                {
                    // Button caps store LogicalMin/Max in a sub-union; if both zero,
                    // the preparsed data convention means "simple 0..1 button".
                    if (c.LogicalMin == 0 && c.LogicalMax == 0)
                    {
                        WriteShortItem(buf, GlobalLogicalMinimum, 0);
                        WriteShortItem(buf, GlobalLogicalMaximum, 1);
                    }
                    else
                    {
                        WriteShortItem(buf, GlobalLogicalMinimum, c.LogicalMin);
                        WriteShortItem(buf, GlobalLogicalMaximum, c.LogicalMax);
                    }

                    WriteShortItem(buf, GlobalReportSize, c.ReportSize);
                    if (!c.IsRange)
                        WriteShortItem(buf, GlobalReportCount, c.ReportCount + reportCount);
                    else
                        WriteShortItem(buf, GlobalReportCount, c.ReportCount);

                    if (lastPhysicalMin != 0)
                    {
                        lastPhysicalMin = 0;
                        WriteShortItem(buf, GlobalPhysicalMinimum, lastPhysicalMin);
                    }
                    if (lastPhysicalMax != 0)
                    {
                        lastPhysicalMax = 0;
                        WriteShortItem(buf, GlobalPhysicalMaximum, lastPhysicalMax);
                    }
                    if (lastUnitExponent != 0)
                    {
                        lastUnitExponent = 0;
                        WriteShortItem(buf, GlobalUnitExponent, lastUnitExponent);
                    }
                    if (lastUnit != 0)
                    {
                        lastUnit = 0;
                        WriteShortItem(buf, GlobalUnit, lastUnit);
                    }

                    byte mainTag = rtIdx switch
                    {
                        HidPreparsedLayout.HidPInput   => MainInput,
                        HidPreparsedLayout.HidPOutput  => MainOutput,
                        HidPreparsedLayout.HidPFeature => MainFeature,
                        _ => MainInput,
                    };
                    WriteShortItem(buf, mainTag, c.BitField);
                    reportCount = 0;
                }
            }
            else
            {
                // Value cap (non-button).
                ref var c = ref caps[capsIdx];
                if (lastReportId != c.ReportID)
                {
                    WriteShortItem(buf, GlobalReportId, c.ReportID);
                    lastReportId = c.ReportID;
                }
                if (c.UsagePage != lastUsagePage)
                {
                    WriteShortItem(buf, GlobalUsagePage, c.UsagePage);
                    lastUsagePage = c.UsagePage;
                }

                if (inhibitWriteOfUsage)
                {
                    inhibitWriteOfUsage = false;
                }
                else
                {
                    if (c.IsRange)
                    {
                        WriteShortItem(buf, LocalUsageMinimum, c.UsageMin);
                        WriteShortItem(buf, LocalUsageMaximum, c.UsageMax);
                    }
                    else
                    {
                        WriteShortItem(buf, LocalUsage, c.UsageMin);
                    }
                }

                if (c.IsDesignatorRange)
                {
                    WriteShortItem(buf, LocalDesignatorMinimum, c.DesignatorMin);
                    WriteShortItem(buf, LocalDesignatorMaximum, c.DesignatorMax);
                }
                else if (c.DesignatorMin != 0)
                {
                    WriteShortItem(buf, LocalDesignatorIndex, c.DesignatorMin);
                }

                if (c.IsStringRange)
                {
                    WriteShortItem(buf, LocalStringMinimum, c.StringMin);
                    WriteShortItem(buf, LocalStringMaximum, c.StringMax);
                }
                else if (c.StringMin != 0)
                {
                    WriteShortItem(buf, LocalString, c.StringMin);
                }

                // In case of a value array, overwrite Report Count per the upstream C.
                if ((c.BitField & 0x02) != 0x02)
                {
                    // Mutates the cap. We use a local copy since caps is an array of structs.
                    c.ReportCount = (ushort)(c.DataIndexMax - c.DataIndexMin + 1);
                    caps[capsIdx] = c;
                }

                // Merge consecutive identical single-value usages.
                if (list.Next != null &&
                    (int)list.Next.MainItemType == rtIdx &&
                    list.Next.TypeOfNode == NodeType.Cap &&
                    !caps[list.Next.CapsIndex].IsButtonCap &&
                    !c.IsRange &&
                    !caps[list.Next.CapsIndex].IsRange &&
                    caps[list.Next.CapsIndex].UsagePage == c.UsagePage &&
                    caps[list.Next.CapsIndex].LogicalMin == c.LogicalMin &&
                    caps[list.Next.CapsIndex].LogicalMax == c.LogicalMax &&
                    caps[list.Next.CapsIndex].PhysicalMin == c.PhysicalMin &&
                    caps[list.Next.CapsIndex].PhysicalMax == c.PhysicalMax &&
                    caps[list.Next.CapsIndex].UnitsExp == c.UnitsExp &&
                    caps[list.Next.CapsIndex].Units == c.Units &&
                    caps[list.Next.CapsIndex].ReportSize == c.ReportSize &&
                    caps[list.Next.CapsIndex].ReportID == c.ReportID &&
                    caps[list.Next.CapsIndex].BitField == c.BitField &&
                    caps[list.Next.CapsIndex].ReportCount == 1 &&
                    c.ReportCount == 1)
                {
                    reportCount++;
                }
                else
                {
                    WriteShortItem(buf, GlobalLogicalMinimum, c.LogicalMin);
                    WriteShortItem(buf, GlobalLogicalMaximum, c.LogicalMax);

                    if (lastPhysicalMin != c.PhysicalMin || lastPhysicalMax != c.PhysicalMax)
                    {
                        WriteShortItem(buf, GlobalPhysicalMinimum, c.PhysicalMin);
                        lastPhysicalMin = c.PhysicalMin;
                        WriteShortItem(buf, GlobalPhysicalMaximum, c.PhysicalMax);
                        lastPhysicalMax = c.PhysicalMax;
                    }

                    if (lastUnitExponent != c.UnitsExp)
                    {
                        WriteShortItem(buf, GlobalUnitExponent, c.UnitsExp);
                        lastUnitExponent = c.UnitsExp;
                    }

                    if (lastUnit != c.Units)
                    {
                        WriteShortItem(buf, GlobalUnit, c.Units);
                        lastUnit = c.Units;
                    }

                    WriteShortItem(buf, GlobalReportSize, c.ReportSize);
                    WriteShortItem(buf, GlobalReportCount, c.ReportCount + reportCount);

                    byte mainTag = rtIdx switch
                    {
                        HidPreparsedLayout.HidPInput   => MainInput,
                        HidPreparsedLayout.HidPOutput  => MainOutput,
                        HidPreparsedLayout.HidPFeature => MainFeature,
                        _ => MainInput,
                    };
                    WriteShortItem(buf, mainTag, c.BitField);
                    reportCount = 0;
                }
            }

            list = list.Next;
        }

        return buf.Bytes.ToArray();
    }
}
