﻿using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace HavenSoft.HexManiac.Core.Models.Runs.Sprites {
   public class LzTilemapRun : LZRun, ISpriteRun {
      SpriteFormat ISpriteRun.SpriteFormat {
         get {
            string hint = null;
            var address = Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, Format.MatchingTileset);
            if (address >= 0 && address < Model.Count) {
               var tileset = Model.GetNextRun(address) as ISpriteRun;
               if (tileset == null) tileset = Model.GetNextRun(arrayTilesetAddress) as ISpriteRun;
               if (tileset != null) hint = tileset.SpriteFormat.PaletteHint;
            }

            return new SpriteFormat(Format.BitsPerPixel, Format.TileWidth, Format.TileHeight, hint);
         }
      }
      public int Pages => 1;
      public TilemapFormat Format { get; }

      /// <summary>
      /// Only allow import if our tileset is actually a tileset
      /// </summary>
      public bool SupportsImport {
         get {
            var tilesetAddress = Model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, Format.MatchingTileset);
            var tileset = Model.GetNextRun(tilesetAddress) as LzTilesetRun;
            if (tileset == null) tileset = Model.GetNextRun(arrayTilesetAddress) as LzTilesetRun;
            return tileset != null;
         }
      }

      public override string FormatString =>
         $"`lzm{Format.BitsPerPixel}x{Format.TileWidth}x{Format.TileHeight}|{Format.MatchingTileset}" +
         (Format.TilesetTableMember != null ? "|" + Format.TilesetTableMember : string.Empty) +
         "`";

      public LzTilemapRun(TilemapFormat format, IDataModel data, int start, SortedSpan<int> sources = null) : base(data, start, sources) {
         Format = format;
      }

      public static bool TryParseTilemapFormat(string format, out TilemapFormat tilemapFormat) {
         tilemapFormat = default;
         if (!(format.StartsWith("`lzm") && format.EndsWith("`"))) return false;
         format = format.Substring(4, format.Length - 5);

         // parse the tilesetHint
         string hint = null, tableMember = null;
         var pipeIndex = format.IndexOf('|');
         if (pipeIndex != -1) {
            hint = format.Substring(pipeIndex + 1);
            format = format.Substring(0, pipeIndex);
            pipeIndex = hint.IndexOf('|');
            if (pipeIndex != -1) {
               tableMember = hint.Substring(pipeIndex + 1);
               hint = hint.Substring(0, pipeIndex);
            }
         }

         var parts = format.Split('x');
         if (parts.Length != 3) return false;
         if (!int.TryParse(parts[0], out int bits)) return false;
         if (!int.TryParse(parts[1], out int width)) return false;
         if (!int.TryParse(parts[2], out int height)) return false;

         tilemapFormat = new TilemapFormat(bits, width, height, hint, tableMember);
         return true;
      }

      public int[,] GetPixels(IDataModel model, int page) {
         var mapData = Decompress(model, Start);
         var tilesetAddress = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, Format.MatchingTileset);
         var tileset = model.GetNextRun(tilesetAddress) as ISpriteRun;
         if (tileset == null) tileset = model.GetNextRun(arrayTilesetAddress) as ISpriteRun;
         
         if (tileset == null || !(tileset is LZRun)) return new int[Format.TileWidth * 8, Format.TileHeight * 8]; // relax the conditions slightly: if the run we found is an LZSpriteRun, that's close enough, we can use it as a tileset.

         var tiles = Decompress(model, tileset.Start);

         return GetPixels(mapData, tiles, Format);
      }

      public static int[,] GetPixels(byte[] mapData, byte[] tiles, TilemapFormat format) {
         var tileSize = format.BitsPerPixel * 8;
         var result = new int[format.TileWidth * 8, format.TileHeight * 8];
         for (int y = 0; y < format.TileHeight; y++) {
            var yStart = y * 8;
            for (int x = 0; x < format.TileWidth; x++) {
               var map = mapData.ReadMultiByteValue((format.TileWidth * y + x) * 2, 2);
               var tile = map & 0x3FF;

               var tileStart = tile * tileSize;
               var pixels = SpriteRun.GetPixels(tiles, tileStart, 1, 1, format.BitsPerPixel); // TODO cache this during this method so we don't load the same tile more than once
               var hFlip = (map >> 10) & 0x1;
               var vFlip = (map >> 11) & 0x1;
               var pal = (map >> 12) & 0xF;
               pal <<= 4;
               var xStart = x * 8;
               for (int yy = 0; yy < 8; yy++) {
                  for (int xx = 0; xx < 8; xx++) {
                     var inX = hFlip == 1 ? 7 - xx : xx;
                     var inY = vFlip == 1 ? 7 - yy : yy;
                     result[xStart + xx, yStart + yy] = pixels[inX, inY] + pal;
                  }
               }
            }
         }

         return result;
      }

      public ISpriteRun SetPixels(IDataModel model, ModelDelta token, int page, int[,] pixels) {
         var tileData = Tilize(pixels, Format.BitsPerPixel);
         var tiles = GetUniqueTiles(tileData);
         var tilesetAddress = model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, Format.MatchingTileset);
         var tileset = model.GetNextRun(tilesetAddress) as LzTilesetRun;
         if (tileset == null) {
            FindMatchingTileset(model);
            tileset = model.GetNextRun(arrayTilesetAddress) as LzTilesetRun;
         }

         var tilesToKeep = new HashSet<int>();
         foreach (var tilemap in tileset.FindDependentTilemaps(model).Except(this)) {
            tilesToKeep.AddRange(tilemap.GetUsedTiles());
         }
         var oldTileDataRaw = Decompress(model, tileset.Start);
         var previousTiles = Tilize(oldTileDataRaw, Format.BitsPerPixel);
         tiles = MergeTilesets(previousTiles, tilesToKeep, tiles);
         tileset.SetPixels(model, token, tiles);
         var mapData = Decompress(model, Start);

         var tileWidth = tileData.GetLength(0);
         var tileHeight = tileData.GetLength(1);

         for (int y = 0; y < tileHeight; y++) {
            for (int x = 0; x < tileWidth; x++) {
               var i = y * tileWidth + x;
               var (tile, paletteIndex) = tileData[x, y];
               var (tileIndex, matchType) = FindMatch(tile, tiles);
               if (tileIndex == -1) tileIndex = 0;
               var mapping = PackMapping(paletteIndex, matchType, tileIndex);
               mapData[i * 2 + 0] = (byte)mapping;
               mapData[i * 2 + 1] = (byte)(mapping >> 8);
            }
         }

         var newModelData = Compress(mapData, 0, mapData.Length);
         var newRun = (ISpriteRun)model.RelocateForExpansion(token, this, newModelData.Count);
         for (int i = 0; i < newModelData.Count; i++) token.ChangeData(model, newRun.Start + i, newModelData[i]);
         for (int i = newModelData.Count; i < Length; i++) token.ChangeData(model, newRun.Start + i, 0xFF);
         newRun = new LzTilemapRun(Format, model, newRun.Start, newRun.PointerSources);
         model.ObserveRunWritten(token, newRun);
         return newRun;
      }

      public IEnumerable<int> GetUsedTiles() {
         var mapData = Decompress(Model, Start);
         for (int y = 0; y < Format.TileHeight; y++) {
            for (int x = 0; x < Format.TileWidth; x++) {
               var map = mapData.ReadMultiByteValue((Format.TileWidth * y + x) * 2, 2);
               var tile = map & 0x3FF;
               yield return tile;
            }
         }
      }

      private int PackMapping(int paletteIndex, TileMatchType matchType, int tileIndex) {
         var hFlip = (matchType == TileMatchType.HFlip || matchType == TileMatchType.BFlip) ? 1 : 0;
         var vFlip = (matchType == TileMatchType.VFlip || matchType == TileMatchType.BFlip) ? 1 : 0;
         return (paletteIndex << 12) | (vFlip << 11) | (hFlip << 10) | tileIndex;
      }

      public static IReadOnlyList<int[,]> MergeTilesets(IReadOnlyList<int[,]> previous, ISet<int> tilesToKeep, IReadOnlyList<int[,]> newTiles) {
         var newListIndex = 0;
         var mergedList = new List<int[,]>();
         for (int i = 0; i < previous.Count; i++) {
            if (tilesToKeep.Contains(i)) {
               mergedList.Add(previous[i]);
            } else {
               while (newListIndex < newTiles.Count && FindMatch(newTiles[newListIndex], mergedList).index != -1) newListIndex += 1;
               if (newListIndex == newTiles.Count) break;
               mergedList.Add(newTiles[newListIndex]);
               newListIndex += 1;
            }
         }
         for (int i = mergedList.Count; i < previous.Count; i++) mergedList.Add(previous[i]);
         for (; newListIndex < newTiles.Count; newListIndex++) {
            if (FindMatch(newTiles[newListIndex], mergedList).index != -1) continue;
            mergedList.Add(newTiles[newListIndex]);
         }
         return mergedList;
      }

      public static IReadOnlyList<int[,]> Tilize(byte[] rawData, int bitsPerPixel) {
         var results = new List<int[,]>();
         var tileSize = 8 * bitsPerPixel;
         for (int i = 0; i < rawData.Length; i += tileSize) {
            results.Add(SpriteRun.GetPixels(rawData, i, 1, 1, bitsPerPixel));
         }
         return results;
      }

      public static (int[,] pixels,int palette)[,] Tilize(int[,] pixels, int bitsPerPixel) {
         var width = pixels.GetLength(0);
         var tileWidth = width / 8;
         var height = pixels.GetLength(1);
         var tileHeight = height / 8;
         var mod = bitsPerPixel == 4 ? 16 : 256;
         var result = new (int[,], int)[tileWidth, tileHeight];
         for(int y = 0; y < tileHeight; y++) {
            var yStart = y * 8;
            for(int x = 0; x < tileWidth; x++) {
               var xStart = x * 8;
               var palette = pixels[xStart, yStart] / mod;
               var tile = new int[8, 8];
               for(int yy = 0; yy < 8; yy++) {
                  for(int xx = 0; xx < 8; xx++) {
                     tile[xx, yy] = pixels[xStart + xx, yStart + yy] % mod;
                  }
               }
               result[x, y] = (tile, palette);
            }
         }
         return result;
      }

      public static IReadOnlyList<int[,]> GetUniqueTiles((int[,] pixels, int palette)[,] tiles) {
         var tileWidth = tiles.GetLength(0);
         var tileHeight = tiles.GetLength(1);
         var result = new List<int[,]> {
            new int[8, 8] // first tile is always the 'empty' tile. Important for transparency/layering stuff.
         };
         for (int y = 0; y < tileHeight; y++) {
            for (int x = 0; x < tileWidth; x++) {
               if (result.Count == 0x400) break; // limite to 0x400 tiles
               var pixels = tiles[x, y].pixels;
               if (result.Any(tile => TilesMatch(tile, pixels) != TileMatchType.None)) continue;
               result.Add(pixels);
            }
         }

         return result;
      }

      public static TileMatchType TilesMatch(int[,] a, int[,] b) {
         Debug.Assert(a.GetLength(0) == 8);
         Debug.Assert(a.GetLength(1) == 8);
         Debug.Assert(b.GetLength(0) == 8);
         Debug.Assert(b.GetLength(1) == 8);

         bool normal_possible = true;
         bool hFlip_possible = true;
         bool vFlip_possible = true;
         bool bFlip_possible = true;

         for (int y = 0; y < 8; y++) {
            for (int x = 0; x < 8; x++) {
               normal_possible &= a[x, y] == b[x, y];
               hFlip_possible &= a[x, y] == b[7 - x, y];
               vFlip_possible &= a[x, y] == b[x, 7 - y];
               bFlip_possible &= a[x, y] == b[7 - x, 7 - y];
            }
            if (!normal_possible && !hFlip_possible && !vFlip_possible && !bFlip_possible) return TileMatchType.None;
         }

         if (normal_possible) return TileMatchType.Normal;
         if (hFlip_possible) return TileMatchType.HFlip;
         if (vFlip_possible) return TileMatchType.VFlip;
         if (bFlip_possible) return TileMatchType.BFlip;
         return TileMatchType.None;
      }

      public static (int index, TileMatchType matchType) FindMatch(int[,] tile, IReadOnlyList<int[,]> collection) {
         for (int i = 0; i < collection.Count; i++) {
            var match = TilesMatch(tile, collection[i]);
            if (match == TileMatchType.None) continue;
            return (i, match);
         }
         return (-1, default);
      }

      private int arrayTilesetAddress;
      public int FindMatchingTileset(IDataModel model) {
         var hint = Format.MatchingTileset;
         IFormattedRun hintRun;
         if (hint != null) {
            hintRun = model.GetNextRun(model.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, hint));
         } else {
            hintRun = model.GetNextRun(PointerSources[0]);
         }

         // easy case: the hint is the address of a tileset
         if (hintRun is LzTilesetRun) {
            arrayTilesetAddress = hintRun.Start;
            return arrayTilesetAddress;
         }

         // harder case: the hint is a table
         if (!(hintRun is ITableRun hintTable)) return hintRun.Start;
         var tilemapPointer = PointerSources[0];
         var tilemapTable = model.GetNextRun(tilemapPointer) as ITableRun;
         if (tilemapTable == null) return hintRun.Start;
         int tilemapIndex = (tilemapPointer - tilemapTable.Start) / tilemapTable.ElementLength;

         // get which element of the table has the tileset
         var segmentOffset = 0;
         for (int i = 0; i < hintTable.ElementContent.Count; i++) {
            if (hintTable.ElementContent[i] is ArrayRunPointerSegment segment) {
               if (Format.TilesetTableMember == null || segment.Name == Format.TilesetTableMember) {
                  if (LzTilesetRun.TryParseTilesetFormat(segment.InnerFormat, out var _)) {
                     var source = hintTable.Start + hintTable.ElementLength * tilemapIndex + segmentOffset;
                     if (model.GetNextRun(model.ReadPointer(source)) is LzTilesetRun tilesetRun) {
                        arrayTilesetAddress = tilesetRun.Start;
                        return arrayTilesetAddress;
                     }
                  }
               }
            }
            segmentOffset += hintTable.ElementContent[i].Length;
         }

         return hintRun.Start;
      }

      protected override BaseRun Clone(SortedSpan<int> newPointerSources) => new LzTilemapRun(Format, Model, Start, newPointerSources);

      public ISpriteRun Duplicate(SpriteFormat format) => new LzSpriteRun(format, Model, Start, PointerSources);

      public LzTilemapRun Duplicate(TilemapFormat format) => new LzTilemapRun(format, Model, Start, PointerSources);
   }

   public enum TileMatchType { None, Normal, HFlip, VFlip, BFlip }
}
