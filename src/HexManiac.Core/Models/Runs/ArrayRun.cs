﻿using HavenSoft.HexManiac.Core.ViewModels.DataFormats;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;

namespace HavenSoft.HexManiac.Core.Models.Runs {
   public enum ElementContentType {
      Unknown,
      PCS,
      Integer,
      Pointer,
   }

   public class ArrayRunElementSegment {
      public string Name { get; }
      public ElementContentType Type { get; }
      public int Length { get; }
      public ArrayRunElementSegment(string name, ElementContentType type, int length) => (Name, Type, Length) = (name, type, length);

      public virtual string ToText(IDataModel rawData, int offset) {
         switch (Type) {
            case ElementContentType.PCS:
               return PCSString.Convert(rawData, offset, Length);
            case ElementContentType.Integer:
               return ToInteger(rawData, offset, Length).ToString();
            case ElementContentType.Pointer:
               var address = rawData.ReadPointer(offset);
               var anchor = rawData.GetAnchorFromAddress(-1, address);
               if (string.IsNullOrEmpty(anchor)) anchor = address.ToString("X6");
               return $"<{anchor}>";
            default:
               throw new NotImplementedException();
         }
      }

      public static int ToInteger(IReadOnlyList<byte> data, int offset, int length) {
         int result = 0;
         int multiplier = 1;
         for (int i = 0; i < length; i++) {
            result += data[offset + i] * multiplier;
            multiplier *= 0x100;
         }
         return result;
      }
   }

   public class ArrayRunEnumSegment : ArrayRunElementSegment {
      public string EnumName { get; }

      public ArrayRunEnumSegment(string name, int length, string enumName) : base(name, ElementContentType.Integer, length) => EnumName = enumName;

      public override string ToText(IDataModel model, int offset) {
         var noChange = new NoDataChangeDeltaModel();
         var options = GetOptions(model);
         if (options == null) return base.ToText(model, offset);

         var resultAsInteger = ToInteger(model, offset, Length);
         if (resultAsInteger >= options.Count) return base.ToText(model, offset);
         var value = options[resultAsInteger];

         // use ~2 postfix for a value if an earlier entry in the array has the same string
         var elementsUpToHereWithThisName = 1;
         for (int i = resultAsInteger - 1; i >= 0; i--) {
            var previousValue = options[i];
            if (previousValue == value) elementsUpToHereWithThisName++;
         }
         if (value.StartsWith("\"") && value.EndsWith("\"")) value = value.Substring(1, value.Length - 2);
         if (elementsUpToHereWithThisName > 1) value += "~" + elementsUpToHereWithThisName;

         // add quotes around it if it contains a space
         if (value.Contains(' ')) value = $"\"{value}\"";

         return value;
      }

      public bool TryParse(IDataModel model, string text, out int value) {
         value = -1;
         text = text.Trim();
         if (text.StartsWith("\"") && text.EndsWith("\"")) text = text.Substring(1, text.Length - 2);
         var partialMatches = new List<string>();
         var matches = new List<string>();
         var noChange = new NoDataChangeDeltaModel();

         // enum must be the name of an array that starts with a string
         var address = model.GetAddressFromAnchor(noChange, -1, EnumName);
         if (address == Pointer.NULL) return false;
         var enumArray = model.GetNextRun(address) as ArrayRun;
         if (enumArray == null) return false;
         if (enumArray.ElementContent.Count == 0) return false;
         var firstContent = enumArray.ElementContent[0];
         if (firstContent.Type != ElementContentType.PCS) return false;

         // if the ~ character is used, expect that it's saying which match we want
         var desiredMatch = 1;
         var splitIndex = text.IndexOf('~');
         if (splitIndex != -1 && !int.TryParse(text.Substring(splitIndex + 1), out desiredMatch)) return false;
         if (splitIndex != -1) text = text.Substring(0, splitIndex);

         // ok, so everything lines up... check the array to see if any values match the string entered
         text = text.ToLower();
         for (int i = 0; i < enumArray.ElementCount; i++) {
            var option = PCSString.Convert(model, enumArray.Start + enumArray.ElementLength * i, firstContent.Length).ToLower();
            option = option.Substring(1, option.Length - 2);
            // check for exact matches
            if (option == text) {
               matches.Add(option);
               if (matches.Count == desiredMatch) {
                  value = i;
                  return true;
               }
            }
            // check for start-of-string matches (for autocomplete)
            if (option.StartsWith(text)) {
               partialMatches.Add(option);
               if(partialMatches.Count==desiredMatch && matches.Count == 0) {
                  value = i;
                  return true;
               }
            }
         }

         // we went through the whole array and didn't find it :(
         return false;
      }

      private IReadOnlyList<string> cachedOptions;
      public IReadOnlyList<string> GetOptions(IDataModel model) {
         if (cachedOptions != null) return cachedOptions;
         var noChange = new NoDataChangeDeltaModel();

         // enum must be the name of an array that starts with a string
         var address = model.GetAddressFromAnchor(noChange, -1, EnumName);
         if (address == Pointer.NULL) return null;
         var enumArray = model.GetNextRun(address) as ArrayRun;
         if (enumArray == null) return null;
         if (enumArray.ElementContent.Count == 0) return null;
         var firstContent = enumArray.ElementContent[0];
         if (firstContent.Type != ElementContentType.PCS) return null;

         // array must be at least as long as than the current value
         var optionCount = enumArray.ElementCount;

         // sweet, we can convert from the integer value to the enum value
         var results = new List<string>();
         for (int i = 0; i < optionCount; i++) {
            var elementStart = enumArray.Start + enumArray.ElementLength * i;
            var valueWithQuotes = PCSString.Convert(model, elementStart, firstContent.Length).Trim();
            var value = valueWithQuotes.Substring(1, valueWithQuotes.Length - 2);
            if (value.Contains(' ')) value = $"\"{value}\"";
            results.Add(value);
         }

         cachedOptions = results;
         return results;
      }
   }

   public class ArrayOffset {
      /// <summary>
      /// Ranges from 0 to ElementCount
      /// </summary>
      public int ElementIndex { get; }
      /// <summary>
      /// Ranges from 0 to ElementContent.Count
      /// </summary>
      public int SegmentIndex { get; }
      /// <summary>
      /// The data address where the current segment starts. Ranges from n to n+ElementContent[SegmentIndex].Length.
      /// </summary>
      public int SegmentStart { get; }
      /// <summary>
      /// The index into the current segment. 0 means the start of the segment.
      /// </summary>
      public int SegmentOffset { get; }
      public ArrayOffset(int elementIndex, int segmentIndex, int segmentStart, int segmentOffset) {
         ElementIndex = elementIndex;
         SegmentIndex = segmentIndex;
         SegmentStart = segmentStart;
         SegmentOffset = segmentOffset;
      }
   }

   public class ColumnHeader {
      public string ColumnTitle { get; }
      public int ByteWidth { get; }
      public ColumnHeader(string title, int byteWidth = 1) => (ColumnTitle, ByteWidth) = (title, byteWidth);
   }

   /// <summary>
   /// Represents a horizontal row of labels.
   /// Each entry is meant to label a column.
   /// </summary>
   public class HeaderRow {
      public IReadOnlyList<ColumnHeader> ColumnHeaders { get; }

      public HeaderRow(ArrayRun source, int byteStart, int length, int startingDataIndex) {
         var headers = new List<ColumnHeader>();
         // we know which 'byte' to start at, but we want to know what 'index' to start at
         // basically, count off each element to figure out how big it is
         int currentByte = 0;
         int startIndex = 0;
         while (currentByte < byteStart) {
            currentByte += source.ElementContent[startIndex % source.ElementContent.Count].Length;
            startIndex++;
         }
         int initialPartialSegmentLength = currentByte - byteStart;
         if (initialPartialSegmentLength > 0) startIndex--;
         currentByte = 0;
         for (int i = 0; currentByte < length; i++) {
            var segment = source.ElementContent[(startIndex + i) % source.ElementContent.Count];
            headers.Add(new ColumnHeader(segment.Name, segment.Length - initialPartialSegmentLength));
            currentByte += segment.Length - initialPartialSegmentLength;
            initialPartialSegmentLength = 0;
         }
         ColumnHeaders = headers;
      }

      public HeaderRow(int start, int length) {
         while (start < 0) start += length;
         var hex = "0123456789ABCDEF";
         var headers = new ColumnHeader[length];
         for (int i = 0; i < length; i++) headers[i] = new ColumnHeader(hex[(start + i) % 0x10].ToString());
         ColumnHeaders = headers;
      }

      public static IReadOnlyList<HeaderRow> GetDefaultColumnHeaders(int columnCount, int startingDataIndex) {
         if (columnCount > 0x10 && columnCount % 0x10 != 0) return new List<HeaderRow>();
         if (columnCount < 0x10 && 0x10 % columnCount != 0) return new List<HeaderRow>();
         if (columnCount >= 0x10) return new[] { new HeaderRow(startingDataIndex, columnCount) };
         return Enumerable.Range(0, 0x10 / columnCount).Select(i => new HeaderRow(columnCount * i + startingDataIndex, columnCount)).ToList();
      }
   }

   public class ArrayRun : BaseRun {
      public const char ExtendArray = '+';
      public const char ArrayStart = '[';
      public const char ArrayEnd = ']';
      public const char SingleByteIntegerFormat = '.';
      public const char DoubleByteIntegerFormat = ':';
      public const char ArrayAnchorSeparator = '/';

      private readonly IDataModel owner;

      // length in bytes of the entire array
      public override int Length { get; }

      public override string FormatString { get; }

      // number of elements in the array
      public int ElementCount { get; }

      // length of each element
      public int ElementLength { get; }

      public string LengthFromAnchor { get; }

      public bool SupportsPointersToElements { get; }

      /// <summary>
      /// For Arrays that support pointers to individual elements within the array,
      /// This is the set of sources that points to each index of the array.
      /// The first set of sources (PointerSourcesForInnerElements[0]) should always be the same as PointerSources.
      /// </summary>
      public IReadOnlyList<IReadOnlyList<int>> PointerSourcesForInnerElements { get; }

      // composition of each element
      public IReadOnlyList<ArrayRunElementSegment> ElementContent { get; }

      private IReadOnlyList<string> cachedElementNames;
      public IReadOnlyList<string> ElementNames {
         get {
            if (cachedElementNames != null) return cachedElementNames;

            var names = new List<string>();
            var source = owner.GetAddressFromAnchor(new NoDataChangeDeltaModel(), -1, LengthFromAnchor);
            if (source == Pointer.NULL) return cachedElementNames = names;
            var sourceArray = owner.GetNextRun(source) as ArrayRun;
            if (sourceArray == null) return cachedElementNames = names;
            if (sourceArray.ElementContent[0].Type != ElementContentType.PCS) return cachedElementNames = names;
            for (int i = 0; i < ElementCount; i++) {
               var nameAddress = sourceArray.Start + sourceArray.ElementLength * i;
               var nameWithQuotes = PCSString.Convert(owner, nameAddress, sourceArray.ElementContent[0].Length).Trim();
               if (nameWithQuotes.Contains(' ')) {
                  names.Add(nameWithQuotes);
               } else if (nameWithQuotes.Length < 2) { // final name could just be a single closing quote and nothing else
                  names.Add(nameWithQuotes);
               } else {
                  var nameWithoutQuotes = nameWithQuotes.Substring(1, nameWithQuotes.Length - 2);
                  names.Add(nameWithoutQuotes);
               }
            }

            return cachedElementNames = names;
         }
      }

      private ArrayRun(IDataModel data, string format, int start, IReadOnlyList<int> pointerSources) : base(start, pointerSources) {
         owner = data;
         FormatString = format;
         SupportsPointersToElements = format.StartsWith(AnchorStart.ToString());
         if (SupportsPointersToElements) format = format.Substring(1);
         var closeArray = format.LastIndexOf(ArrayEnd.ToString());
         if (!format.StartsWith(ArrayStart.ToString()) || closeArray == -1) throw new ArrayRunParseException($"Array Content must be wrapped in {ArrayStart}{ArrayEnd}.");
         var segments = format.Substring(1, closeArray - 1);
         var length = format.Substring(closeArray + 1);
         ElementContent = ParseSegments(segments);
         if (ElementContent.Count == 0) throw new ArrayRunParseException("Array Content must not be empty.");
         ElementLength = ElementContent.Sum(e => e.Length);

         if (length.Length == 0) {
            var nextRun = owner.GetNextRun(Start);
            while (nextRun is NoInfoRun && nextRun.Start < owner.Count) nextRun = owner.GetNextRun(nextRun.Start + 1);
            var byteLength = 0;
            var elementCount = 0;
            while (Start + byteLength + ElementLength <= nextRun.Start && DataMatchesElementFormat(owner, Start + byteLength, ElementContent, nextRun)) {
               byteLength += ElementLength;
               elementCount++;
            }
            LengthFromAnchor = string.Empty;
            ElementCount = elementCount;
         } else if (int.TryParse(length, out int result)) {
            // fixed length is easy
            LengthFromAnchor = string.Empty;
            ElementCount = result;
         } else {
            LengthFromAnchor = length;
            ElementCount = ParseLengthFromAnchor();
         }

         Length = ElementLength * ElementCount;
      }

      private ArrayRun(IDataModel data, string format, string lengthFromAnchor, int start, int elementCount, IReadOnlyList<ArrayRunElementSegment> segments, IReadOnlyList<int> pointerSources, IReadOnlyList<IReadOnlyList<int>> pointerSourcesForInnerElements) : base(start, pointerSources) {
         owner = data;
         FormatString = format;
         ElementContent = segments;
         ElementLength = ElementContent.Sum(e => e.Length);
         ElementCount = elementCount;
         var closeArray = format.LastIndexOf(ArrayEnd.ToString());
         LengthFromAnchor = lengthFromAnchor;
         Length = ElementLength * ElementCount;
         SupportsPointersToElements = pointerSourcesForInnerElements != null;
         PointerSourcesForInnerElements = pointerSourcesForInnerElements;
      }

      public static ErrorInfo TryParse(IDataModel data, string format, int start, IReadOnlyList<int> pointerSources, out ArrayRun self) {
         try {
            self = new ArrayRun(data, format, start, pointerSources);
         } catch (ArrayRunParseException e) {
            self = null;
            return new ErrorInfo(e.Message);
         }

         return ErrorInfo.NoError;
      }

      public static bool TrySearch(IDataModel data, ModelDelta changeToken, string originalFormat, out ArrayRun self, Func<IFormattedRun, bool> runFilter = null) {
         self = null;
         var format = originalFormat;
         var allowPointersToEntries = format.StartsWith(AnchorStart.ToString());
         if (allowPointersToEntries) format = format.Substring(1);
         var closeArray = format.LastIndexOf(ArrayEnd.ToString());
         if (!format.StartsWith(ArrayStart.ToString()) || closeArray == -1) throw new ArrayRunParseException($"Array Content must be wrapped in {ArrayStart}{ArrayEnd}");
         var segments = format.Substring(1, closeArray - 1);
         var length = format.Substring(closeArray + 1);
         var elementContent = ParseSegments(segments);
         if (elementContent.Count == 0) return false;
         var elementLength = elementContent.Sum(e => e.Length);

         if (string.IsNullOrEmpty(length)) {
            var bestAddress = StandardSearch(data, elementContent, elementLength, out int bestLength);
            if (bestAddress == Pointer.NULL) return false;
            self = new ArrayRun(data, originalFormat + bestLength, string.Empty, bestAddress, bestLength, elementContent, data.GetNextRun(bestAddress).PointerSources, null);
         } else {
            var bestAddress = KnownLengthSearch(data, elementContent, elementLength, length, out int bestLength, runFilter);
            if (bestAddress == Pointer.NULL) return false;
            var lengthFromAnchor = int.TryParse(length, out var _) ? string.Empty : length;
            self = new ArrayRun(data, originalFormat, lengthFromAnchor, bestAddress, bestLength, elementContent, data.GetNextRun(bestAddress).PointerSources, null);
         }

         if (allowPointersToEntries) self = self.AddSourcesPointingWithinArray(changeToken);
         return true;
      }

      private static int StandardSearch(IDataModel data, List<ArrayRunElementSegment> elementContent, int elementLength, out int bestLength) {
         int bestAddress = Pointer.NULL;
         bestLength = 0;

         var run = data.GetNextAnchor(0);
         for (var nextRun = data.GetNextAnchor(run.Start + run.Length); run.Start < int.MaxValue; nextRun = data.GetNextAnchor(nextRun.Start + nextRun.Length)) {
            if (run is ArrayRun || run.PointerSources == null) {
               run = nextRun;
               continue;
            }
            var nextArray = nextRun;

            int currentLength = 0;
            int currentAddress = run.Start;
            while (true) {
               if (nextArray.Start < currentAddress) nextArray = data.GetNextAnchor(nextArray.Start + 1);
               if (DataMatchesElementFormat(data, currentAddress, elementContent, nextArray)) {
                  currentLength++;
                  currentAddress += elementLength;
               } else {
                  break;
               }
            }
            if (bestLength < currentLength) {
               bestLength = currentLength;
               bestAddress = run.Start;
            }

            run = nextRun;
         }

         return bestAddress;
      }

      private static int KnownLengthSearch(IDataModel data, List<ArrayRunElementSegment> elementContent, int elementLength, string lengthToken, out int bestLength, Func<IFormattedRun, bool> runFilter) {
         var noChange = new NoDataChangeDeltaModel();
         if (!int.TryParse(lengthToken, out bestLength)) {
            var matchedArrayName = lengthToken;
            var matchedArrayAddress = data.GetAddressFromAnchor(noChange, -1, matchedArrayName);
            if (matchedArrayAddress == Pointer.NULL) return Pointer.NULL;
            var matchedRun = data.GetNextRun(matchedArrayAddress) as ArrayRun;
            if (matchedRun == null) return Pointer.NULL;
            bestLength = matchedRun.ElementCount;
         }

         for (var run = data.GetNextRun(0); run.Start < data.Count; run = data.GetNextRun(run.Start + run.Length + 1)) {
            if (!(run is PointerRun)) continue;
            var targetRun = data.GetNextRun(data.ReadPointer(run.Start));
            if (targetRun is ArrayRun) continue;

            // some searches allow special conditions on the run. For example, we could only be intersted in runs with >100 pointers leading to it.
            if (runFilter != null && !runFilter(targetRun)) continue;

            int currentLength = 0;
            int currentAddress = targetRun.Start;
            bool earlyExit = false;
            for (int i = 0; i < bestLength; i++) {
               var nextArray = data.GetNextAnchor(currentAddress + 1);
               if (DataMatchesElementFormat(data, currentAddress, elementContent, nextArray)) {
                  currentLength++;
                  currentAddress += elementLength;
               } else {
                  earlyExit = true;
                  break;
               }
            }

            if (!earlyExit) return targetRun.Start;
         }

         return Pointer.NULL;
      }

      private string cachedCurrentString;
      private int currentCachedStartIndex = -1, currentCachedIndex = -1;
      public override IDataFormat CreateDataFormat(IDataModel data, int index) {
         var offsets = ConvertByteOffsetToArrayOffset(index);
         var currentSegment = ElementContent[offsets.SegmentIndex];
         if (currentSegment.Type == ElementContentType.PCS) {
            if (currentCachedStartIndex != offsets.SegmentStart || currentCachedIndex > offsets.SegmentOffset) {
               currentCachedStartIndex = offsets.SegmentStart;
               currentCachedIndex = offsets.SegmentOffset;
               cachedCurrentString = PCSString.Convert(data, offsets.SegmentStart, currentSegment.Length);
            }

            return PCSRun.CreatePCSFormat(data, offsets.SegmentStart, index, cachedCurrentString);
         }

         if (currentSegment.Type == ElementContentType.Integer) {
            if (currentSegment is ArrayRunEnumSegment enumSegment) {
               var value = enumSegment.ToText(data, index);
               return new IntegerEnum(offsets.SegmentStart, index, value, currentSegment.Length);
            } else {
               var value = ArrayRunElementSegment.ToInteger(data, offsets.SegmentStart, currentSegment.Length);
               return new Integer(offsets.SegmentStart, index, value, currentSegment.Length);
            }
         }

         if (currentSegment.Type == ElementContentType.Pointer) {
            var destination = data.ReadPointer(offsets.SegmentStart);
            var destinationName = data.GetAnchorFromAddress(offsets.SegmentStart, destination);
            return new Pointer(offsets.SegmentStart, index - offsets.SegmentStart, destination, destinationName);
         }

         throw new NotImplementedException();
      }

      public ArrayOffset ConvertByteOffsetToArrayOffset(int byteOffset) {
         var offset = byteOffset - Start;
         int elementIndex = offset / ElementLength;
         int elementOffset = offset % ElementLength;
         int segmentIndex = 0, segmentOffset = elementOffset;
         while (ElementContent[segmentIndex].Length <= segmentOffset) {
            segmentOffset -= ElementContent[segmentIndex].Length; segmentIndex++;
         }
         return new ArrayOffset(elementIndex, segmentIndex, byteOffset - segmentOffset, segmentOffset);
      }

      public ArrayRun Append(int elementCount) {
         var lastArrayCharacterIndex = FormatString.LastIndexOf(ArrayEnd);
         var newFormat = FormatString.Substring(0, lastArrayCharacterIndex + 1);
         if (newFormat != FormatString) newFormat += ElementCount + elementCount;
         return new ArrayRun(owner, newFormat, LengthFromAnchor, Start, ElementCount + elementCount, ElementContent, PointerSources, PointerSourcesForInnerElements);
      }

      public ArrayRun AddSourcePointingWithinArray(int source) {
         var destination = owner.ReadPointer(source);
         var index = (destination - Start) / ElementLength;
         if (index < 0 || index >= ElementCount) throw new IndexOutOfRangeException();
         if (index == 0) throw new NotImplementedException();
         var newInnerPointerSources = PointerSourcesForInnerElements.ToList();
         newInnerPointerSources[index] = newInnerPointerSources[index].Concat(new[] { source }).ToList();
         return new ArrayRun(owner, FormatString, LengthFromAnchor, Start, ElementCount, ElementContent, PointerSources, newInnerPointerSources);
      }

      public ArrayRun AddSourcesPointingWithinArray(ModelDelta changeToken) {
         if (ElementCount < 2) return this;

         var destinations = new int[ElementCount - 1];
         for (int i = 1; i < ElementCount; i++) destinations[i - 1] = Start + ElementLength * i;

         var sources = owner.SearchForPointersToAnchor(changeToken, destinations);

         var results = new List<List<int>>();
         results.Add(PointerSources?.ToList() ?? new List<int>());
         for (int i = 1; i < ElementCount; i++) results.Add(new List<int>());

         foreach (var source in sources) {
            var destination = owner.ReadPointer(source);
            int destinationIndex = (destination - Start) / ElementLength;
            results[destinationIndex].Add(source);
         }

         var pointerSourcesForInnerElements = results.Cast<IReadOnlyList<int>>().ToList();
         return new ArrayRun(owner, FormatString, LengthFromAnchor, Start, ElementCount, ElementContent, PointerSources, pointerSourcesForInnerElements);
      }

      /// <summary>
      /// Arrays might want custom column headers.
      /// If so, this method can get them.
      /// </summary>
      public IReadOnlyList<HeaderRow> GetColumnHeaders(int columnCount, int startingDataIndex) {
         // check if it's a multiple of the array width
         if (columnCount >= ElementLength) {
            if (columnCount % ElementLength != 0) return null;
            return new[] { new HeaderRow(this, startingDataIndex - Start, columnCount, startingDataIndex) };
         }

         // check if it's a divisor of the array width
         if (ElementLength % columnCount != 0) return null;
         var segments = ElementLength / columnCount;
         return Enumerable.Range(0, segments).Select(i => new HeaderRow(this, columnCount * i + startingDataIndex - Start, columnCount, startingDataIndex)).ToList();
      }

      public void AppendTo(IDataModel data, StringBuilder text, int start, int length) {
         var offsets = ConvertByteOffsetToArrayOffset(start);
         length += offsets.SegmentOffset;
         for (int i = offsets.ElementIndex; i < ElementCount && length > 0; i++) {
            var offset = Start + i * ElementLength;
            if (offsets.SegmentIndex == 0) text.Append(ExtendArray);
            for (int j = offsets.SegmentIndex; j < ElementContent.Count && length > 0; j++) {
               var segment = ElementContent[j];
               text.Append(segment.ToText(data, offset).Trim());
               text.Append(" ");
               offset += segment.Length;
               length -= segment.Length;
            }
            text.Append(Environment.NewLine);
            offsets = new ArrayOffset(0, 0, 0, 0);
         }
      }

      public IFormattedRun Move(int newStart) {
         return new ArrayRun(owner, FormatString, LengthFromAnchor, newStart, ElementCount, ElementContent, PointerSources, PointerSourcesForInnerElements);
      }

      public override IFormattedRun RemoveSource(int source) {
         if (!SupportsPointersToElements) return base.RemoveSource(source);
         var newPointerSources = PointerSources.Where(item => item != source).ToList();
         var newInnerPointerSources = new List<IReadOnlyList<int>>();
         foreach (var list in PointerSourcesForInnerElements) {
            newInnerPointerSources.Add(list.Where(item => item != source).ToList());
         }

         return new ArrayRun(owner, FormatString, LengthFromAnchor, Start, ElementCount, ElementContent, newPointerSources, newInnerPointerSources);
      }

      protected override IFormattedRun Clone(IReadOnlyList<int> newPointerSources) {
         // since the inner pointer sources includes the first row, update the first row
         List<IReadOnlyList<int>> newInnerPointerSources = null;
         if (PointerSourcesForInnerElements != null) {
            newInnerPointerSources = new List<IReadOnlyList<int>>();
            newInnerPointerSources.Add(newPointerSources);
            for (int i = 1; i < PointerSourcesForInnerElements.Count; i++) newInnerPointerSources.Add(PointerSourcesForInnerElements[i]);
         }

         return new ArrayRun(owner, FormatString, LengthFromAnchor, Start, ElementCount, ElementContent, newPointerSources, newInnerPointerSources);
      }

      private static List<ArrayRunElementSegment> ParseSegments(string segments) {
         var list = new List<ArrayRunElementSegment>();
         segments = segments.Trim();
         while (segments.Length > 0) {
            int nameEnd = 0;
            while (nameEnd < segments.Length && char.IsLetterOrDigit(segments[nameEnd])) nameEnd++;
            var name = segments.Substring(0, nameEnd);
            if (name == string.Empty) throw new ArrayRunParseException("expected name, but none was found: " + segments);
            segments = segments.Substring(nameEnd);
            var format = ElementContentType.Unknown;
            int formatLength = 0;
            int segmentLength = 0;
            if (segments.Length >= 2 && segments.Substring(0, 2) == "\"\"") {
               format = ElementContentType.PCS;
               formatLength = 2;
               while (formatLength < segments.Length && char.IsDigit(segments[formatLength])) formatLength++;
               segmentLength = int.Parse(segments.Substring(2, formatLength - 2));
            } else if (segments.StartsWith(DoubleByteIntegerFormat + string.Empty + DoubleByteIntegerFormat)) {
               (format, formatLength, segmentLength) = (ElementContentType.Integer, 2, 4);
            } else if (segments.StartsWith(DoubleByteIntegerFormat + string.Empty + SingleByteIntegerFormat) || segments.StartsWith(".:")) {
               (format, formatLength, segmentLength) = (ElementContentType.Integer, 2, 3);
            } else if (segments.StartsWith(DoubleByteIntegerFormat.ToString())) {
               (format, formatLength, segmentLength) = (ElementContentType.Integer, 1, 2);
            } else if (segments.StartsWith(SingleByteIntegerFormat.ToString())) {
               (format, formatLength, segmentLength) = (ElementContentType.Integer, 1, 1);
            } else if (segments.StartsWith(PointerRun.PointerStart + string.Empty + PointerRun.PointerEnd)) {
               (format, formatLength, segmentLength) = (ElementContentType.Pointer, 2, 4);
            }

            // check to see if a name or length is part of the format
            if (format == ElementContentType.Integer && segments.Length > formatLength && segments[formatLength] != ' ') {
               segments = segments.Substring(formatLength);
               if (int.TryParse(segments, out var maxValue)) {
                  throw new NotImplementedException();
               } else {
                  var endOfToken = segments.IndexOf(' ');
                  if (endOfToken == -1) endOfToken = segments.Length;
                  var enumName = segments.Substring(0, endOfToken);
                  segments = segments.Substring(endOfToken).Trim();
                  list.Add(new ArrayRunEnumSegment(name, segmentLength, enumName));
               }
            } else {
               if (format == ElementContentType.Unknown) throw new ArrayRunParseException($"Could not parse format '{segments}'");
               segments = segments.Substring(formatLength).Trim();
               list.Add(new ArrayRunElementSegment(name, format, segmentLength));
            }
         }

         return list;
      }

      private int ParseLengthFromAnchor() {
         // length is based on another array
         int address = owner.GetAddressFromAnchor(new ModelDelta(), -1, LengthFromAnchor);
         if (address == Pointer.NULL) {
            // the requested name was unknown... length is zero for now
            return 0;
         }

         var run = owner.GetNextRun(address) as ArrayRun;
         if (run == null || run.Start != address) {
            // the requested name was not an array, or did not start where anticipated
            // length is zero for now
            return 0;
         }

         return run.ElementCount;
      }

      private static bool DataMatchesElementFormat(IDataModel owner, int start, IReadOnlyList<ArrayRunElementSegment> segments, IFormattedRun nextAnchor) {
         foreach (var segment in segments) {
            if (start + segment.Length > owner.Count) return false;
            if (!DataMatchesSegmentFormat(owner, start, segment, nextAnchor)) return false;
            start += segment.Length;
         }
         return true;
      }

      private static bool DataMatchesSegmentFormat(IDataModel owner, int start, ArrayRunElementSegment segment, IFormattedRun nextAnchor) {
         if (start + segment.Length > nextAnchor.Start && nextAnchor is ArrayRun) return false; // don't blap over existing arrays
         switch (segment.Type) {
            case ElementContentType.PCS:
               int readLength = PCSString.ReadString(owner, start, true, segment.Length);
               if (readLength == -1) return false;
               if (readLength > segment.Length) return false;
               if (Enumerable.Range(start, segment.Length).All(i => owner[i] == 0xFF)) return false;
               if (!Enumerable.Range(start + readLength, segment.Length - readLength).All(i => owner[i] == 0x00 || owner[i] == 0xFF)) return false;
               return true;
            case ElementContentType.Integer:
               if (segment is ArrayRunEnumSegment enumSegment) {
                  return owner.ReadMultiByteValue(start, segment.Length) < enumSegment.GetOptions(owner).Count;
               } else {
                  return true;
               }
            case ElementContentType.Pointer:
               var destination = owner.ReadPointer(start);
               if (destination == Pointer.NULL) return true;
               return 0 <= destination && destination <= owner.Count;
            default:
               throw new NotImplementedException();
         }
      }
   }

   public class ArrayRunParseException : Exception {
      public ArrayRunParseException(string message) : base(message) { }
   }
}
