// The paired check's store of v2.0.3 sets (DeliveryModel.ExploreAligned):
// sets of v2.0.3 state numbers as hash-consed binary tries. A leaf holds 64
// numbers as a bitmap, an inner node its two halves, and every distinct
// node is stored once, so sets that share a run of numbers share its
// nodes. A set's trie stops at the lowest level that covers its largest
// number. That makes a set's trie unique: equal sets have the same root,
// and the root's number names the set. Hash consing as in Filliâtre and
// Conchon, "Type-safe modular hash-consing" (ML Workshop 2006).

using System;
using System.Collections.Generic;
using System.Numerics;

internal sealed class SetStore
{
    // Node n, in pages of 2^PageBits: its word is a leaf's bitmap, or an
    // inner node's halves as left << 32 | right, 0 for an empty half. Its
    // level is 0 for a leaf, and the level's top bit marks the root of a
    // stored set. Node 0 is the empty set. Pages are added as nodes are, so
    // the store never copies them.
    const int PageBits = 20, PageMask = (1 << PageBits) - 1;
    readonly List<ulong[]> words = new();
    readonly List<byte[]> levels = new();
    int nodes = 1;
    // Open addressing over the node numbers, 0 free, at most 0.7 full.
    int[] table = new int[1 << 12];
    // Interning buffers: the sorted numbers, then each level's positions
    // and nodes.
    int[] items = new int[256], pos = new int[256], ids = new int[256];

    const byte RootBit = 0x80;

    public SetStore()
    {
        words.Add(new ulong[1 << PageBits]);
        levels.Add(new byte[1 << PageBits]);
    }

    public int Sets { get; private set; }
    public long Members { get; private set; }
    public int Nodes => nodes;
    public long Bytes => (long)words.Count * (1 << PageBits) * (sizeof(ulong) + sizeof(byte)) + (long)table.Length * sizeof(int);

    ulong Word(int x) => words[x >> PageBits][x & PageMask];
    int Lv(int x) => levels[x >> PageBits][x & PageMask] & ~RootBit;
    int Left(int x) => (int)(Word(x) >> 32);
    int Right(int x) => (int)(uint)Word(x);

    static int Hash(ulong w, int lv)
    {
        ulong z = (w ^ (ulong)lv << 58) * 0x9E3779B97F4A7C15UL;
        z ^= z >> 29;
        z *= 0xBF58476D1CE4E5B9UL;
        z ^= z >> 32;
        return (int)z;
    }

    int Node(ulong w, int lv)
    {
        int m = table.Length - 1;
        int h = Hash(w, lv) & m;
        for (int x; (x = table[h]) != 0; h = (h + 1) & m)
            if (Word(x) == w && Lv(x) == lv) return x;
        int id = nodes++;
        if ((id >> PageBits) == words.Count)
        {
            words.Add(new ulong[1 << PageBits]);
            levels.Add(new byte[1 << PageBits]);
        }
        words[id >> PageBits][id & PageMask] = w;
        levels[id >> PageBits][id & PageMask] = (byte)lv;
        if ((long)nodes * 10 > (long)table.Length * 7) Rehash();
        else table[h] = id;
        return id;
    }

    void Rehash()
    {
        table = new int[table.Length * 2];
        int m = table.Length - 1;
        for (int x = 1; x < nodes; x++)
        {
            int h = Hash(Word(x), Lv(x)) & m;
            while (table[h] != 0) h = (h + 1) & m;
            table[h] = x;
        }
    }

    // The set holding the numbers in h, stored if new. Its number is its
    // root node's.
    public int Intern(HashSet<int> h)
    {
        int n = h.Count;
        if (n == 0) return 0;
        if (items.Length < n)
        {
            int size = Math.Max(n, items.Length * 2);
            items = new int[size]; pos = new int[size]; ids = new int[size];
        }
        h.CopyTo(items);
        Array.Sort(items, 0, n);
        int k = 0;
        for (int i = 0; i < n;)
        {
            int blk = items[i] >> 6;
            ulong bits = 0;
            for (; i < n && items[i] >> 6 == blk; i++) bits |= 1UL << (items[i] & 63);
            pos[k] = blk;
            ids[k] = Node(bits, 0);
            k++;
        }
        // Pair the nodes up a level at a time until one covers them all.
        // The writes trail the reads, so the buffers are reused in place.
        int lv = 0;
        while (k > 1 || pos[0] != 0)
        {
            lv++;
            int u = 0;
            for (int j = 0; j < k; u++)
            {
                int p = pos[j], l = 0, r = 0;
                if ((p & 1) == 0)
                {
                    l = ids[j++];
                    if (j < k && pos[j] == p + 1) r = ids[j++];
                }
                else r = ids[j++];
                pos[u] = p >> 1;
                ids[u] = Node((ulong)(uint)l << 32 | (uint)r, lv);
            }
            k = u;
        }
        int root = ids[0];
        ref byte mark = ref levels[root >> PageBits][root & PageMask];
        if ((mark & RootBit) == 0)
        {
            mark |= RootBit;
            Sets++;
            Members += n;
        }
        return root;
    }

    // The numbers of a set, ascending, into dst.
    public List<int> Fill(int set, List<int> dst)
    {
        dst.Clear();
        if (set != 0) Walk(set, 0, dst);
        return dst;
    }

    void Walk(int x, int p, List<int> dst)
    {
        if (Lv(x) == 0)
        {
            for (ulong m = Word(x); m != 0; m &= m - 1)
                dst.Add(p * 64 + BitOperations.TrailingZeroCount(m));
            return;
        }
        int l = Left(x), r = Right(x);
        if (l != 0) Walk(l, 2 * p, dst);
        if (r != 0) Walk(r, 2 * p + 1, dst);
    }

    // Set a is a subset of set b. A trie on a higher level than b's holds a
    // number above all of b's. One on a lower level is compared with b's
    // left half at its level.
    public bool Subset(int a, int b)
    {
        if (a == b || a == 0) return true;
        if (b == 0) return false;
        int la = Lv(a), lb = Lv(b);
        if (la > lb) return false;
        for (; lb > la; lb--)
        {
            b = Left(b);
            if (b == 0) return false;
        }
        return Sub(a, b);
    }

    bool Sub(int x, int y)
    {
        if (x == y || x == 0) return true;
        if (y == 0) return false;
        if (Lv(x) == 0) return (Word(x) & ~Word(y)) == 0;
        return Sub(Left(x), Left(y)) && Sub(Right(x), Right(y));
    }
}
