// Copyright (C) 2025, The Duplicati Team
// https://duplicati.com, hello@duplicati.com
// 
// Permission is hereby granted, free of charge, to any person obtaining a 
// copy of this software and associated documentation files (the "Software"), 
// to deal in the Software without restriction, including without limitation 
// the rights to use, copy, modify, merge, publish, distribute, sublicense, 
// and/or sell copies of the Software, and to permit persons to whom the 
// Software is furnished to do so, subject to the following conditions:
// 
// The above copyright notice and this permission notice shall be included in 
// all copies or substantial portions of the Software.
// 
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS 
// OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, 
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE 
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER 
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING 
// FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER 
// DEALINGS IN THE SOFTWARE.

#nullable enable

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Duplicati.Library.Common.IO;
using Duplicati.StreamUtil;

namespace Duplicati.Library.Utility
{
    public static class Utility
    {
        /// <summary>
        /// Size of buffers for copying stream
        /// </summary>
        public static long DEFAULT_BUFFER_SIZE => SystemContextSettings.Buffersize;

        /// <summary>
        /// A cache of the FileSystemCaseSensitive property, which is computed upon the first access.
        /// </summary>
        private static bool? CachedIsFSCaseSensitive;

        /// <summary>
        /// The EPOCH offset (unix style)
        /// </summary>
        public static readonly DateTime EPOCH = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        /// <summary>
        /// The attribute value used to indicate error
        /// </summary>
        public const FileAttributes ATTRIBUTE_ERROR = (FileAttributes)(1 << 30);

        /// <summary>
        /// The callback delegate type used to collecting file information
        /// </summary>
        /// <param name="rootpath">The path that the file enumeration started at</param>
        /// <param name="path">The current element</param>
        /// <param name="attributes">The attributes of the element</param>
        /// <returns>A value indicating if the folder should be recursed, ignored for other types</returns>
        public delegate bool EnumerationFilterDelegate(string rootpath, string path, FileAttributes attributes);

        /// <summary>
        /// Copies the content of one stream into another
        /// </summary>
        /// <param name="source">The stream to read from</param>
        /// <param name="target">The stream to write to</param>
        public static long CopyStream(Stream source, Stream target)
        {
            return CopyStream(source, target, true);
        }

        /// <summary>
        /// Copies the content of one stream into another
        /// </summary>
        /// <param name="source">The stream to read from</param>
        /// <param name="target">The stream to write to</param>
        /// <param name="tryRewindSource">True if an attempt should be made to rewind the source stream, false otherwise</param>
        /// <param name="buf">Temporary buffer to use (optional)</param>
        public static long CopyStream(Stream source, Stream target, bool tryRewindSource, byte[]? buf = null)
        {
            if (tryRewindSource && source.CanSeek)
                try { source.Position = 0; }
                catch
                {
                    // ignored
                }

            buf = buf ?? new byte[DEFAULT_BUFFER_SIZE];

            int read;
            long total = 0;
            while ((read = source.Read(buf, 0, buf.Length)) != 0)
            {
                target.Write(buf, 0, read);
                total += read;
            }

            return total;
        }

        /// <summary>
        /// Copies the content of one stream into another
        /// </summary>
        /// <param name="source">The stream to read from</param>
        /// <param name="target">The stream to write to</param>
        /// <param name="cancelToken">Token to cancel the operation.</param>
        public static async Task<long> CopyStreamAsync(Stream source, Stream target, CancellationToken cancelToken)
        {
            return await CopyStreamAsync(source, target, tryRewindSource: true, cancelToken: cancelToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Copies the content of one stream into another
        /// </summary>
        /// <param name="source">The stream to read from</param>
        /// <param name="target">The stream to write to</param>
        /// <param name="tryRewindSource">True if an attempt should be made to rewind the source stream, false otherwise</param>
        /// <param name="cancelToken">Token to cancel the operation.</param>
        /// <param name="buf">Temporary buffer to use (optional)</param>
        public static async Task<long> CopyStreamAsync(Stream source, Stream target, bool tryRewindSource, CancellationToken cancelToken, byte[]? buf = null)
        {
            if (tryRewindSource && source.CanSeek)
                try { source.Position = 0; }
                catch { }

            buf = buf ?? new byte[DEFAULT_BUFFER_SIZE];

            int read;
            long total = 0;
            while (true)
            {
                read = await source.ReadAsync(buf, 0, buf.Length, cancelToken).ConfigureAwait(false);
                if (read == 0) break;
                await target.WriteAsync(buf, 0, read, cancelToken).ConfigureAwait(false);
                total += read;
            }

            return total;
        }

        /// <summary>
        /// Get the length of a stream.
        /// Attempt to use the stream's Position property if allowPositionFallback is <c>true</c> (only valid if stream is at the end).
        /// </summary>
        /// <param name="stream">Stream to get the length of.</param>
        /// <param name="allowPositionFallback">Attempt to use the Position property if <c>true</c> and the Length property is not available (only valid if stream is at the end).</param>
        /// <returns>Returns the stream's length, if available, or null if not supported by the stream.</returns>
        public static long? GetStreamLength(Stream stream, bool allowPositionFallback = true)
        {
            return GetStreamLength(stream, out bool _, allowPositionFallback);
        }

        /// <summary>
        /// Get the length of a stream.
        /// Attempt to use the stream's Position property if allowPositionFallback is <c>true</c> (only valid if stream is at the end).
        /// </summary>
        /// <param name="stream">Stream to get the length of.</param>
        /// <param name="isStreamPosition">Indicates if the Position value was used instead of Length.</param>
        /// <param name="allowPositionFallback">Attempt to use the Position property if <c>true</c> and the Length property is not available (only valid if stream is at the end).</param>
        /// <returns>Returns the stream's length, if available, or null if not supported by the stream.</returns>
        public static long? GetStreamLength(Stream stream, out bool isStreamPosition, bool allowPositionFallback = true)
        {
            isStreamPosition = false;
            long? streamLength = null;
            try { streamLength = stream.Length; } catch { }
            if (!streamLength.HasValue && allowPositionFallback)
            {
                try
                {
                    // Hack: This is a fall-back method to detect the source stream size, assuming the current position is the end of the stream.
                    streamLength = stream.Position;
                    isStreamPosition = true;
                }
                catch { } // 
            }

            return streamLength;
        }

        /// <summary>
        /// These are characters that must be escaped when using a globbing expression
        /// </summary>
        private static readonly string BADCHARS = @"\\|\+|\||\{|\[|\(|\)|\]|\}|\^|\$|\#|\.";

        /// <summary>
        /// Most people will probably want to use fileglobbing, but RegExp's are more flexible.
        /// By converting from the weak globbing to the stronger regexp, we support both.
        /// </summary>
        /// <param name="globexp"></param>
        /// <returns></returns>
        public static string ConvertGlobbingToRegExp(string globexp)
        {
            //First escape all special characters
            globexp = Regex.Replace(globexp, BADCHARS, @"\$&");

            //Replace the globbing expressions with the corresponding regular expressions
            globexp = globexp.Replace('?', '.').Replace("*", ".*");
            return "^" + globexp + "$";
        }

        /// <summary>
        /// Convert literal path to the equivalent regular expression.
        /// </summary>
        public static string ConvertLiteralToRegExp(string literalPath)
        {
            // Escape all special characters
            return Regex.Escape(literalPath);
        }

        /// <summary>
        /// Returns a list of all files found in the given folder.
        /// The search is recursive.
        /// </summary>
        /// <param name="basepath">The folder to look in</param>
        /// <returns>A list of the full filenames</returns>
        public static IEnumerable<string> EnumerateFiles(string basepath)
        {
            return EnumerateFileSystemEntries(basepath).Where(x => !x.EndsWith(Util.DirectorySeparatorString, StringComparison.Ordinal));
        }

        /// <summary>
        /// Returns a list of folder names found in the given folder.
        /// The search is recursive.
        /// </summary>
        /// <param name="basepath">The folder to look in</param>
        /// <returns>A list of the full paths</returns>
        public static IEnumerable<string> EnumerateFolders(string basepath)
        {
            return EnumerateFileSystemEntries(basepath).Where(x => x.EndsWith(Util.DirectorySeparatorString, StringComparison.Ordinal));
        }

        /// <summary>
        /// Returns a list of all files and subfolders found in the given folder.
        /// The search is recursive.
        /// </summary>
        /// <param name="basepath">The folder to look in.</param>
        /// <returns>A list of the full filenames and foldernames. Foldernames ends with the directoryseparator char</returns>
        public static IEnumerable<string> EnumerateFileSystemEntries(string basepath)
        {
            return EnumerateFileSystemEntries(basepath, SystemIO.IO_OS.GetDirectories, Directory.GetFiles, null);
        }

        /// <summary>
        /// A callback delegate used for applying alternate enumeration of filesystems
        /// </summary>
        /// <param name="path">The path to return data from</param>
        /// <returns>A list of paths</returns>
        public delegate string[] FileSystemInteraction(string path);

        /// <summary>
        /// A callback delegate used for extracting attributes from a file or folder
        /// </summary>
        /// <param name="path">The path to return data from</param>
        /// <returns>Attributes for the file or folder</returns>
        public delegate FileAttributes ExtractFileAttributes(string path);

        /// <summary>
        /// A callback delegate used for extracting attributes from a file or folder
        /// </summary>
        /// <param name="rootpath">The root folder where the path was found</param>
        /// <param name="path">The path that produced the error</param>
        /// <param name="ex">The exception for the error</param>
        public delegate void ReportAccessError(string rootpath, string path, Exception ex);

        /// <summary>
        /// Returns a list of all files found in the given folder.
        /// The search is recursive.
        /// </summary>
        /// <param name="rootpath">The folder to look in</param>
        /// <param name="folderList">A function to call that lists all folders in the supplied folder</param>
        /// <param name="fileList">A function to call that lists all files in the supplied folder</param>
        /// <param name="attributeReader">A function to call that obtains the attributes for an element, set to null to avoid reading attributes</param>
        /// <param name="errorCallback">An optional function to call with error messages.</param>
        /// <returns>A list of the full filenames</returns>
        public static IEnumerable<string> EnumerateFileSystemEntries(string rootpath, FileSystemInteraction folderList, FileSystemInteraction fileList, ExtractFileAttributes? attributeReader, ReportAccessError? errorCallback = null)
        {
            var lst = new Stack<string>();

            if (IsFolder(rootpath, attributeReader))
            {
                rootpath = Util.AppendDirSeparator(rootpath);
                try
                {
                    var attr = attributeReader?.Invoke(rootpath) ?? FileAttributes.Directory;
                    lst.Push(rootpath);
                }
                catch (Exception ex) when (!ex.IsAbortException())
                {
                    errorCallback?.Invoke(rootpath, rootpath, ex);
                }

                while (lst.Count > 0)
                {
                    var f = lst.Pop();

                    yield return f;

                    try
                    {
                        foreach (var s in folderList(f))
                        {
                            var sf = Util.AppendDirSeparator(s);
                            try
                            {
                                var attr = attributeReader?.Invoke(sf) ?? FileAttributes.Directory;
                                lst.Push(sf);
                            }
                            catch (Exception ex) when (!ex.IsAbortException())
                            {
                                errorCallback?.Invoke(rootpath, sf, ex);
                            }
                        }
                    }
                    catch (Exception ex) when (!ex.IsAbortException())
                    {
                        errorCallback?.Invoke(rootpath, f, ex);
                    }

                    string[]? files = null;
                    if (fileList != null)
                    {
                        try
                        {
                            files = fileList(f);
                        }
                        catch (Exception ex) when (!ex.IsAbortException())
                        {
                            errorCallback?.Invoke(rootpath, f, ex);
                        }
                    }

                    if (files != null)
                    {
                        foreach (var s in files)
                            yield return s;
                    }
                }
            }
            else
            {
                yield return rootpath;
            }
        }

        /// <summary>
        /// Test if specified path is a folder
        /// </summary>
        /// <param name="path">Path to test</param>
        /// <param name="attributeReader">Function to use for testing path</param>
        /// <returns>True if path is refers to a folder</returns>
        public static bool IsFolder(string path, ExtractFileAttributes? attributeReader)
        {
            if (attributeReader == null)
                return true;

            try
            {
                return attributeReader(path).HasFlag(FileAttributes.Directory);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Tests if path refers to a file, or folder, <b>below</b> the parent folder
        /// </summary>
        /// <param name="fileOrFolderPath">File or folder to test</param>
        /// <param name="parentFolder">Candidate parent folder</param>
        /// <returns>True if below parent folder, false otherwise
        /// (note that this returns false if the two argument paths are identical!)</returns>
        public static bool IsPathBelowFolder(string fileOrFolderPath, string parentFolder)
        {
            var sanitizedParentFolder = Util.AppendDirSeparator(parentFolder);
            return fileOrFolderPath.StartsWith(sanitizedParentFolder, ClientFilenameStringComparison) &&
                   !fileOrFolderPath.Equals(sanitizedParentFolder, ClientFilenameStringComparison);
        }

        /// <summary>
        /// Returns parent folder of path
        /// </summary>
        /// <param name="path">Full file or folder path</param>
        /// <param name="forceTrailingDirectorySeparator">If true, return value always has trailing separator</param>
        /// <returns>Parent folder of path (containing folder for file paths, parent folder for folder paths)</returns>
        public static string? GetParent(string path, bool forceTrailingDirectorySeparator)
        {
            var len = path.Length - 1;
            if (len > 1 && path[len] == Path.DirectorySeparatorChar)
            {
                len--;
            }

            var last = path.LastIndexOf(Path.DirectorySeparatorChar, len);
            if (last == -1 || last == 0 && len == 0)
                return null;

            if (last == 0 && !OperatingSystem.IsWindows())
                return Util.DirectorySeparatorString;

            var parent = path.Substring(0, last);

            if (forceTrailingDirectorySeparator ||
                OperatingSystem.IsWindows() && parent.Length == 2 && parent[1] == ':' && char.IsLetter(parent[0]))
            {
                parent += Path.DirectorySeparatorChar;
            }

            return parent;
        }



        /// <summary>
        /// Given a collection of unique folders, returns only parent-most folders
        /// </summary>
        /// <param name="folders">Collection of unique folders</param>
        /// <returns>Parent-most folders of input collection</returns>
        public static IEnumerable<string> SimplifyFolderList(ICollection<string> folders)
        {
            if (!folders.Any())
                return folders;

            var result = new LinkedList<string>();
            result.AddFirst(folders.First());

            foreach (var folder1 in folders)
            {
                bool addFolder = true;
                LinkedListNode<string>? next;
                for (var node = result.First; node != null; node = next)
                {
                    next = node.Next;
                    var folder2 = node.Value;

                    if (IsPathBelowFolder(folder1, folder2))
                    {
                        // higher-level folder already present
                        addFolder = false;
                        break;
                    }

                    if (IsPathBelowFolder(folder2, folder1))
                    {
                        // retain folder1
                        result.Remove(node);
                    }
                }

                if (addFolder)
                {
                    result.AddFirst(folder1);
                }
            }

            return result.Distinct();
        }

        /// <summary>
        /// Given a collection of file paths, return those NOT contained within specified collection of folders
        /// </summary>
        /// <param name="files">Collection of files to filter</param>
        /// <param name="folders">Collection of folders to use as filter</param>
        /// <returns>Files not in any of specified <c>folders</c></returns>
        public static IEnumerable<string> GetFilesNotInFolders(IEnumerable<string> files, IEnumerable<string> folders)
        {
            return files.Where(x => folders.All(folder => !IsPathBelowFolder(x, folder)));
        }

        /// <summary>
        /// Calculates the size of files in a given folder
        /// </summary>
        /// <param name="folder">The folder to examine</param>
        /// <returns>The combined size of all files that match the filter</returns>
        public static long GetDirectorySize(string folder)
        {
            return EnumerateFolders(folder).Sum((path) => new FileInfo(path).Length);
        }

        /// <summary>
        /// Some streams can return a number that is less than the requested number of bytes.
        /// This is usually due to fragmentation, and is solved by issuing a new read.
        /// This function wraps that functionality.
        /// </summary>
        /// <param name="stream">The stream to read</param>
        /// <param name="buf">The buffer to read into</param>
        /// <param name="count">The amount of bytes to read</param>
        /// <returns>The actual number of bytes read</returns>
        public static int ForceStreamRead(Stream stream, byte[] buf, int count)
        {
            int a;
            int index = 0;
            do
            {
                a = stream.Read(buf, index, count);
                index += a;
                count -= a;
            } while (a != 0 && count > 0);

            return index;
        }

        /// <summary>
        /// Some streams can return a number that is less than the requested number of bytes.
        /// This is usually due to fragmentation, and is solved by issuing a new read.
        /// This function wraps that functionality.
        /// </summary>
        /// <param name="stream">The stream to read.</param>
        /// <param name="buf">The buffer to read into.</param>
        /// <param name="count">The amount of bytes to read.</param>
        /// <returns>The number of bytes read</returns>
        public static async Task<int> ForceStreamReadAsync(this System.IO.Stream stream, byte[] buf, int count)
        {
            int a;
            int index = 0;
            do
            {
                a = await stream.ReadAsync(buf, index, count).ConfigureAwait(false);
                index += a;
                count -= a;
            } while (a != 0 && count > 0);

            return index;
        }

        /// <summary>
        /// Compares two streams to see if they are binary equals
        /// </summary>
        /// <param name="stream1">One stream</param>
        /// <param name="stream2">Another stream</param>
        /// <param name="checkLength">True if the length of the two streams should be compared</param>
        /// <returns>True if they are equal, false otherwise</returns>
        public static bool CompareStreams(Stream stream1, Stream stream2, bool checkLength)
        {
            if (checkLength)
            {
                try
                {
                    if (stream1.Length != stream2.Length)
                        return false;
                }
                catch
                {
                    //We must read along, trying to determine if they are equals
                }
            }

            int longSize = BitConverter.GetBytes((long)0).Length;
            byte[] buf1 = new byte[longSize * 512];
            byte[] buf2 = new byte[buf1.Length];

            int a1, a2;
            while ((a1 = ForceStreamRead(stream1, buf1, buf1.Length)) == (a2 = ForceStreamRead(stream2, buf2, buf2.Length)))
            {
                int ix = 0;
                for (int i = 0; i < a1 / longSize; i++)
                    if (BitConverter.ToUInt64(buf1, ix) != BitConverter.ToUInt64(buf2, ix))
                        return false;
                    else
                        ix += longSize;

                for (int i = 0; i < a1 % longSize; i++)
                    if (buf1[ix] != buf2[ix])
                        return false;
                    else
                        ix++;

                if (a1 == 0)
                    break;
            }

            return a1 == a2;
        }

        /// <summary>
        /// Reads a file, attempts to detect encoding
        /// </summary>
        /// <param name="filename">The path to the file to read</param>
        /// <returns>The file contents</returns>
        public static string ReadFileWithDefaultEncoding(string filename)
        {
            // Since StreamReader defaults to UTF8 and most text files will NOT be UTF8 without BOM,
            // we need to detect the encoding (at least that it's not UTF8).
            // So we read the first 4096 bytes and try to decode them as UTF8. 
            var buffer = new byte[4096];
            using (var file = new FileStream(filename, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                Utility.ForceStreamRead(file, buffer, 4096);
            }

            var enc = Encoding.UTF8;
            try
            {
                // this will throw an error if not really UTF8
                // ReSharper disable once ReturnValueOfPureMethodIsNotUsed
                new UTF8Encoding(false, true).GetString(buffer);
            }
            catch (Exception)
            {
                enc = Encoding.Default;
            }

            // This will load the text using the BOM, or the detected encoding if no BOM.
            using (var reader = new StreamReader(filename, enc, true))
            {
                // Remove all \r from the file and split on \n, then pass directly to ExtractOptions
                return reader.ReadToEnd();
            }
        }

        /// <summary>
        /// Formats a size into a human readable format, e.g. 2048 becomes &quot;2 KB&quot; or -2283 becomes &quot;-2.23 KB%quot.
        /// </summary>
        /// <param name="size">The size to format</param>
        /// <returns>A human readable string representing the size</returns>
        public static string FormatSizeString(double size)
        {
            double sizeAbs = Math.Abs(size);  // Allow formatting of negative sizes
            if (sizeAbs >= 1024 * 1024 * 1024 * 1024L)
                return Strings.Utility.FormatStringTB(size / (1024 * 1024 * 1024 * 1024L));
            else if (sizeAbs >= 1024 * 1024 * 1024)
                return Strings.Utility.FormatStringGB(size / (1024 * 1024 * 1024));
            else if (sizeAbs >= 1024 * 1024)
                return Strings.Utility.FormatStringMB(size / (1024 * 1024));
            else if (sizeAbs >= 1024)
                return Strings.Utility.FormatStringKB(size / 1024);
            else
                return Strings.Utility.FormatStringB((long)size); // safe to cast because lower than 1024 and thus well within range of long
        }

        /// <summary>
        /// Parses a string into a boolean value.
        /// </summary>
        /// <param name="value">The value to parse.</param>
        /// <param name="defaultFunc">A delegate that returns the default value if <paramref name="value"/> is not a valid boolean value.</param>
        /// <returns>The parsed value, or the value returned by <paramref name="defaultFunc"/>.</returns>
        public static bool ParseBool(string? value, Func<bool> defaultFunc)
        {
            if (String.IsNullOrWhiteSpace(value))
            {
                return defaultFunc();
            }

            switch (value.Trim().ToLower(CultureInfo.InvariantCulture))
            {
                case "1":
                case "on":
                case "true":
                case "yes":
                    return true;
                case "0":
                case "off":
                case "false":
                case "no":
                    return false;
                default:
                    return defaultFunc();
            }
        }

        /// <summary>
        /// Parses a string into a boolean value.
        /// </summary>
        /// <param name="value">The value to parse.</param>
        /// <param name="default">The default value, in case <paramref name="value"/> is not a valid boolean value.</param>
        /// <returns>The parsed value, or the default value.</returns>
        public static bool ParseBool(string? value, bool @default)
        {
            return ParseBool(value, () => @default);
        }

        /// <summary>
        /// Parses an option from the option set, using the convention that if the option is set, it is true unless it parses to false, and false otherwise
        /// </summary>
        /// <param name="options">The set of options to look for the setting in</param>
        /// <param name="value">The value to look for in the settings</param>
        /// <returns>The parsed value, or the default value (<c>false</c>).</returns>
        public static bool ParseBoolOption(IReadOnlyDictionary<string, string?> options, string value)
        {
            if (options.TryGetValue(value, out var opt))
                return ParseBool(opt, true);
            else
                return false;
        }

        /// <summary>
        /// Parses an integer option from the option set, returning the default value if the option is not found or cannot be parsed
        /// </summary>
        /// <param name="options">The set of options to look for the setting in</param>
        /// <param name="value">The value to look for in the settings</param>
        /// <param name="default">The default value to return if there are no matches.</param>
        /// <returns>The parsed or default integer value.</returns>
        public static TimeSpan ParseTimespanOption(IReadOnlyDictionary<string, string?> options, string value, string @default)
        {
            var opt = options.GetValueOrDefault(value);
            if (string.IsNullOrWhiteSpace(opt))
                opt = @default;

            return Timeparser.ParseTimeSpan(opt);
        }

        /// <summary>
        /// Parses an enum found in the options dictionary
        /// </summary>
        /// <returns>The parsed or default enum value.</returns>
        /// <param name="options">The set of options to look for the setting in</param>
        /// <param name="value">The value to look for in the settings</param>
        /// <param name="default">The default value to return if there are no matches.</param>
        /// <typeparam name="T">The enum type parameter.</typeparam>
        public static T ParseEnumOption<T>(IReadOnlyDictionary<string, string?> options, string value, T @default) where T : struct, Enum
        {
            return options.TryGetValue(value, out var opt) ? ParseEnum(opt, @default) : @default;
        }

        /// <summary>
        /// Parses a flags-type enum found in the options dictionary
        /// </summary>
        /// <returns>The parsed or default enum value.</returns>
        /// <param name="options">The set of options to look for the setting in</param>
        /// <param name="value">The value to look for in the settings</param>
        /// <param name="default">The default value to return if there are no matches.</param>
        /// <typeparam name="T">The enum type parameter.</typeparam>
        public static T ParseFlagsOption<T>(IReadOnlyDictionary<string, string?> options, string value, T @default) where T : struct, Enum
        {
            return options.TryGetValue(value, out var opt) ? ParseEnum(opt, @default) : @default;
        }

        /// <summary>
        /// Attempts to parse an enum with case-insensitive lookup, returning the default value if there was no match
        /// </summary>
        /// <returns>The parsed or default enum value.</returns>
        /// <param name="value">The string to parse.</param>
        /// <param name="default">The default value to return if there are no matches.</param>
        /// <typeparam name="T">The enum type parameter.</typeparam>
        public static T ParseEnum<T>(string? value, T @default) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
                return @default;
            foreach (var s in Enum.GetNames(typeof(T)))
                if (s.Equals(value, StringComparison.OrdinalIgnoreCase))
                    return (T)Enum.Parse(typeof(T), s);

            return @default;
        }

        /// <summary>
        /// Parses a string into a flags enum value.
        /// </summary>
        /// <typeparam name="T">The enum type to parse.</typeparam>
        /// <param name="value">The value to parse.</param>
        /// <param name="default">The default value to return if there are no matches.</param>
        /// <returns></returns>
        public static T ParseFlags<T>(string? value, T @default) where T : struct, Enum
        {
            if (string.IsNullOrWhiteSpace(value))
                return @default;

            var flags = 0;
            foreach (var s in value.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var trimmed = s.Trim();
                if (Enum.TryParse(trimmed, true, out T flag))
                    flags = flags | (int)(object)flag;
            }

            return (T)(object)flags;
        }

        /// <summary> 
        /// Parses an option with int value, returning the default value if the option is not found or cannot be parsed 
        /// </summary> 
        /// <param name="options">The set of options to look for the setting in</param> 
        /// <param name="value">The value to look for in the settings</param> 
        /// <param name="default">default value</param> 
        /// <returns></returns> 
        public static int ParseIntOption(IReadOnlyDictionary<string, string?> options, string value, int @default)
            => options.TryGetValue(value, out var opt) && int.TryParse(opt ?? string.Empty, out var result) ? result : @default;

        /// <summary> 
        /// Parses an option with long value, returning the default value if the option is not found or cannot be parsed 
        /// </summary> 
        /// <param name="options">The set of options to look for the setting in</param> 
        /// <param name="value">The value to look for in the settings</param> 
        /// <param name="default">default value</param> 
        /// <returns></returns> 
        public static long ParseLongOption(IReadOnlyDictionary<string, string?> options, string value, long @default)
            => options.TryGetValue(value, out var opt) && long.TryParse(opt ?? string.Empty, out var result) ? result : @default;

        /// <summary>
        /// Parses a size option from the option set, returning the default value if the option is not found or cannot be parsed
        /// </summary>
        /// <param name="options">The set of options to look for the setting in</param>
        /// <param name="value">The value to look for in the settings</param>
        /// <param name="defaultMultiplier">Multiplier to use if the value does not have a multiplier</param>
        /// <param name="default">The default value to return if there are no matches.</param>
        /// <returns>The parsed or default size value.</returns>
        public static long ParseSizeOption(IReadOnlyDictionary<string, string?> options, string value, string defaultMultiplier, string @default)
        {
            var opt = options.GetValueOrDefault(value);
            if (string.IsNullOrWhiteSpace(opt))
                opt = @default;

            return Sizeparser.ParseSize(opt, defaultMultiplier);
        }

        /// <summary>
        /// Converts a sequence of bytes to a hex string
        /// </summary>
        /// <returns>The array as hex string.</returns>
        /// <param name="data">The data to convert</param>
        public static string ByteArrayAsHexString(byte[] data)
        {
            return BitConverter.ToString(data).Replace("-", string.Empty);
        }

        /// <summary>
        /// Converts a hex string to a byte array
        /// </summary>
        /// <returns>The string as byte array.</returns>
        /// <param name="hex">The hex string</param>
        /// <param name="data">The parsed data</param>
        public static void HexStringAsByteArray(string hex, byte[] data)
        {
            for (var i = 0; i < hex.Length; i += 2)
                data[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
        }

        /// <summary>
        /// Converts a hex string to a byte array, as a function so no variable declaration on caller's side is needed
        /// </summary>
        /// <returns>The string as byte array.</returns>
        /// <param name="hex">The hex string</param>
        public static byte[] HexStringAsByteArray(string hex)
        {
            var data = new byte[hex.Length / 2];
            HexStringAsByteArray(hex, data);
            return data;
        }

        [SupportedOSPlatform("linux")]
        [SupportedOSPlatform("macos")]
        /// <summary>
        /// Invokes the &quot;which&quot; command to determine if a given application is available in the path
        /// </summary>
        /// <param name="appname">The name of the application to look for</param>
        public static bool Which(string appname)
        {
            if (!(OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()))
                return false;

            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo("which", appname)
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = false,
                    RedirectStandardInput = false,
                    UseShellExecute = false
                };

                var pi = System.Diagnostics.Process.Start(psi)
                    ?? throw new Exception("Unexpected failure to start process");
                pi.WaitForExit(5000);
                if (pi.HasExited)
                    return pi.ExitCode == 0;
                else
                    return false;
            }
            catch
            {
            }

            return false;
        }


        /// <value>
        /// Returns a value indicating if the filesystem, is case sensitive 
        /// </value>
        public static bool IsFSCaseSensitive
        {
            get
            {
                if (!CachedIsFSCaseSensitive.HasValue)
                {
                    var str = Environment.GetEnvironmentVariable("FILESYSTEM_CASE_SENSITIVE");

                    // TODO: This should probably be determined by filesystem rather than OS,
                    // OSX can actually have the disks formatted as Case Sensitive, but insensitive is default
                    CachedIsFSCaseSensitive = ParseBool(str, () => OperatingSystem.IsLinux());
                }

                return CachedIsFSCaseSensitive.Value;
            }
        }

        /// <summary>
        /// Gets a string comparer that matches the client filesystems case sensitivity
        /// </summary>
        public static StringComparer ClientFilenameStringComparer => IsFSCaseSensitive ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase;

        /// <summary>
        /// Gets the string comparision that matches the client filesystems case sensitivity
        /// </summary>
        public static StringComparison ClientFilenameStringComparison => IsFSCaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

        /// <summary>
        /// The path to the users home directory
        /// </summary>
        public static readonly string HOME_PATH = Environment.GetFolderPath(!OperatingSystem.IsWindows() ? Environment.SpecialFolder.Personal : Environment.SpecialFolder.UserProfile);

        /// <summary>
        /// Regexp for matching environment variables on Windows (%VAR%)
        /// </summary>
        private static readonly Regex ENVIRONMENT_VARIABLE_MATCHER_WINDOWS = new Regex(@"\%(?<name>\w+)\%");

        /// <summary>
        /// Expands environment variables in a RegExp safe format
        /// </summary>
        /// <returns>The expanded string.</returns>
        /// <param name="str">The string to expand.</param>
        /// <param name="lookup">A lookup method that converts an environment key to an expanded string</param>
        public static string? ExpandEnvironmentVariablesRegexp(string? str, Func<string?, string?>? lookup = null)
        {
            if (string.IsNullOrWhiteSpace(str))
                return str;

            if (lookup == null)
                lookup = x => Environment.GetEnvironmentVariable(x ?? string.Empty);

            return

                // TODO: Should we switch to using the native format ($VAR or ${VAR}), instead of following the Windows scheme?
                // IsClientLinux ? new Regex(@"\$(?<name>\w+)|(\{(?<name>[^\}]+)\})") : ENVIRONMENT_VARIABLE_MATCHER_WINDOWS

                ENVIRONMENT_VARIABLE_MATCHER_WINDOWS.Replace(str, m => Regex.Escape(lookup(m.Groups["name"].Value) ?? string.Empty));
        }

        /// <summary>
        /// Normalizes a DateTime instance by converting to UTC and flooring to seconds.
        /// </summary>
        /// <returns>The normalized date time</returns>
        /// <param name="input">The input time</param>
        public static DateTime NormalizeDateTime(DateTime input)
        {
            var ticks = input.ToUniversalTime().Ticks;
            ticks -= ticks % TimeSpan.TicksPerSecond;
            return new DateTime(ticks, DateTimeKind.Utc);
        }

        /// <summary>
        /// Given a DateTime instance, return the number of elapsed seconds since the Unix epoch
        /// </summary>
        /// <returns>The number of elapsed seconds since the Unix epoch</returns>
        /// <param name="input">The input time</param>
        public static long NormalizeDateTimeToEpochSeconds(DateTime input)
        {
            // Note that we cannot return (new DateTimeOffset(input)).ToUnixTimeSeconds() here.
            // The DateTimeOffset constructor will convert the provided DateTime to the UTC
            // equivalent. However, if DateTime.MinValue is provided (for example, when creating
            // a new backup), this can result in values that fall outside the DateTimeOffset.MinValue
            // and DateTimeOffset.MaxValue bounds.
            return (long)Math.Floor((NormalizeDateTime(input) - EPOCH).TotalSeconds);
        }

        /// <summary>
        /// The format string for a DateTime
        /// </summary>
        //Note: Actually the K should be Z which is more correct as it is forced to be Z, but Z as a format specifier is fairly undocumented
        public static string SERIALIZED_DATE_TIME_FORMAT = "yyyyMMdd'T'HHmmssK";

        /// <summary>
        /// Returns a string representation of a <see cref="System.DateTime"/> in UTC format
        /// </summary>
        /// <param name="dt">The <see cref="System.DateTime"/> instance</param>
        /// <returns>A string representing the time</returns>
        public static string SerializeDateTime(DateTime dt)
        {
            return dt.ToUniversalTime().ToString(SERIALIZED_DATE_TIME_FORMAT, System.Globalization.CultureInfo.InvariantCulture);
        }

        /// <summary>
        /// Parses a serialized <see cref="System.DateTime"/> instance
        /// </summary>
        /// <param name="str">The string to parse</param>
        /// <returns>The parsed <see cref="System.DateTime"/> instance</returns>
        public static bool TryDeserializeDateTime(string str, out DateTime dt)
        {
            return DateTime.TryParseExact(str, SERIALIZED_DATE_TIME_FORMAT, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out dt);
        }

        /// <summary>
        /// Parses a serialized <see cref="System.DateTime"/> instance
        /// </summary>
        /// <param name="str">The string to parse</param>
        /// <returns>The parsed <see cref="System.DateTime"/> instance</returns>
        public static DateTime DeserializeDateTime(string str)
        {
            if (!TryDeserializeDateTime(str, out var dt))
                throw new Exception(Strings.Utility.InvalidDateError(str));

            return dt;
        }

        /// <summary>
        /// Gets the unique items from a collection.
        /// </summary>
        /// <typeparam name="T">The type of the elements in <paramref name="collection"/>.</typeparam>
        /// <param name="collection">The collection to remove duplicate items from.</param>
        /// <param name="duplicateItems">The duplicate items in <paramref name="collection"/>.</param>
        /// <returns>The unique items from <paramref name="collection"/>.</returns>
        public static IList<T> GetUniqueItems<T>(IEnumerable<T> collection, out ISet<T> duplicateItems)
        {
            return GetUniqueItems(collection, EqualityComparer<T>.Default, out duplicateItems);
        }

        /// <summary>
        /// Gets the unique items from a collection.
        /// </summary>
        /// <typeparam name="T">The type of the elements in <paramref name="collection"/>.</typeparam>
        /// <param name="collection">The collection to remove duplicate items from.</param>
        /// <param name="comparer">The <see cref="System.Collections.Generic.IEqualityComparer{T}"/> implementation to use when comparing values in the collection.</param>
        /// <param name="duplicateItems">The duplicate items in <paramref name="collection"/>.</param>
        /// <returns>The unique items from <paramref name="collection"/>.</returns>
        public static IList<T> GetUniqueItems<T>(IEnumerable<T> collection, IEqualityComparer<T> comparer, out ISet<T> duplicateItems)
        {
            var uniqueItems = new HashSet<T>(comparer);
            var results = new List<T>();
            duplicateItems = new HashSet<T>(comparer);

            foreach (var item in collection)
            {
                if (uniqueItems.Add(item))
                    results.Add(item);
                else
                    duplicateItems.Add(item);
            }

            return results;
        }

        // <summary>
        // Returns the entry assembly or reasonable approximation if no entry assembly is available.
        // This is the case in NUnit tests. The following approach does not work w/ Mono due to unimplemented members:
        // http://social.msdn.microsoft.com/Forums/nb-NO/clr/thread/db44fe1a-3bb4-41d4-a0e0-f3021f30e56f
        // so this layer of indirection is necessary
        // </summary>
        // <returns>entry assembly or reasonable approximation</returns>
        public static System.Reflection.Assembly getEntryAssembly()
        {
            return System.Reflection.Assembly.GetEntryAssembly() ?? System.Reflection.Assembly.GetExecutingAssembly();
        }

        /// <summary>
        /// Converts a Base64 encoded string to &quot;base64 for url&quot;
        /// See https://en.wikipedia.org/wiki/Base64#URL_applications
        /// </summary>
        /// <param name="data">The base64 encoded string</param>
        /// <returns>The base64 for url encoded string</returns>
        public static string Base64PlainToBase64Url(string data)
        {
            return data.Replace('+', '-').Replace('/', '_');
        }

        /// <summary>
        /// Converts a &quot;base64 for url&quot; encoded string to a Base64 encoded string.
        /// See https://en.wikipedia.org/wiki/Base64#URL_applications
        /// </summary>
        /// <param name="data">The base64 for url encoded string</param>
        /// <returns>The base64 encoded string</returns>
        public static string Base64UrlToBase64Plain(string data)
        {
            return data.Replace('-', '+').Replace('_', '/');
        }

        /// <summary>
        /// Encodes a byte array into a &quot;base64 for url&quot; encoded string.
        /// See https://en.wikipedia.org/wiki/Base64#URL_applications
        /// </summary>
        /// <param name="data">The data to encode</param>
        /// <returns>The base64 for url encoded string</returns>
        public static string Base64UrlEncode(byte[] data)
        {
            return Base64PlainToBase64Url(Convert.ToBase64String(data));
        }

        /// <summary>
        /// Converts a DateTime instance to a Unix timestamp
        /// </summary>
        /// <returns>The Unix timestamp.</returns>
        /// <param name="input">The DateTime instance to convert.</param>
        public static long ToUnixTimestamp(DateTime input)
        {
            var ticks = input.ToUniversalTime().Ticks;
            ticks -= ticks % TimeSpan.TicksPerSecond;
            input = new DateTime(ticks, DateTimeKind.Utc);

            return (long)Math.Floor((input - EPOCH).TotalSeconds);
        }

        /// <summary>
        /// Returns a value indicating if the given type should be treated as a primitive
        /// </summary>
        /// <returns><c>true</c>, if type is primitive for serialization, <c>false</c> otherwise.</returns>
        /// <param name="t">The type to check.</param>
        private static bool IsPrimitiveTypeForSerialization(Type t)
        {
            return t.IsPrimitive || t.IsEnum || t == typeof(string) || t == typeof(DateTime) || t == typeof(TimeSpan);
        }

        /// <summary>
        /// Writes a primitive to the output, or returns false if the input is not primitive
        /// </summary>
        /// <returns><c>true</c>, the item was printed, <c>false</c> otherwise.</returns>
        /// <param name="item">The item to write.</param>
        /// <param name="writer">The target writer.</param>
        private static bool PrintSerializeIfPrimitive(object? item, TextWriter writer)
        {
            if (item == null)
            {
                writer.Write("null");
                return true;
            }

            if (IsPrimitiveTypeForSerialization(item.GetType()))
            {
                if (item is DateTime time)
                {
                    writer.Write(time.ToLocalTime());
                    writer.Write(" (");
                    writer.Write(ToUnixTimestamp(time));
                    writer.Write(")");
                }
                else
                    writer.Write(item);
                return true;
            }

            return false;
        }

        /// <summary>
        /// Prints the object to a stream, which can be used for display or logging
        /// </summary>
        /// <returns>The serialized object</returns>
        /// <param name="item">The object to serialize</param>
        /// <param name="writer">The writer to write the results to</param>
        /// <param name="filter">A filter applied to properties to decide if they are omitted or not</param>
        /// <param name="recurseobjects">A value indicating if non-primitive values are recursed</param>
        /// <param name="indentation">The string indentation</param>
        /// <param name="visited">A lookup table with visited objects, used to avoid infinite recursion</param>
        /// <param name="collectionlimit">The maximum number of items to report from an IEnumerable instance</param>
        public static void PrintSerializeObject(object? item, TextWriter writer, Func<System.Reflection.PropertyInfo, object, bool>? filter = null, bool recurseobjects = false, int indentation = 0, int collectionlimit = 0, Dictionary<object, object?>? visited = null)
        {
            visited = visited ?? new Dictionary<object, object?>();
            var indentstring = new string(' ', indentation);

            var first = true;

            if (item == null || IsPrimitiveTypeForSerialization(item.GetType()))
            {
                writer.Write(indentstring);
                if (PrintSerializeIfPrimitive(item, writer))
                    return;
            }

            if (item == null)
                return;

            foreach (var p in item.GetType().GetProperties())
            {
                if (filter != null && !filter(p, item))
                    continue;

                if (IsPrimitiveTypeForSerialization(p.PropertyType))
                {
                    if (first)
                        first = false;
                    else
                        writer.WriteLine();

                    writer.Write("{0}{1}: ", indentstring, p.Name);
                    PrintSerializeIfPrimitive(p.GetValue(item, null), writer);
                }
                else if (typeof(Task).IsAssignableFrom(p.PropertyType) || p.Name == "TaskReader")
                {
                    // Ignore Task items
                    continue;
                }
                else if (typeof(System.Collections.IEnumerable).IsAssignableFrom(p.PropertyType))
                {
                    var enumerable = p.GetValue(item, null) as System.Collections.IEnumerable;
                    var any = false;
                    if (enumerable != null)
                    {
                        var enumerator = enumerable.GetEnumerator();
                        if (enumerator != null)
                        {
                            var remain = collectionlimit;

                            if (first)
                                first = false;
                            else
                                writer.WriteLine();

                            writer.Write("{0}{1}: [", indentstring, p.Name);
                            if (enumerator.MoveNext())
                            {
                                any = true;
                                writer.WriteLine();
                                PrintSerializeObject(enumerator.Current, writer, filter, recurseobjects, indentation + 4, collectionlimit, visited);

                                remain--;

                                while (enumerator.MoveNext())
                                {
                                    writer.WriteLine(",");

                                    if (remain == 0)
                                    {
                                        writer.Write("...");
                                        break;
                                    }

                                    PrintSerializeObject(enumerator.Current, writer, filter, recurseobjects, indentation + 4, collectionlimit, visited);

                                    remain--;
                                }

                            }

                            if (any)
                            {
                                writer.WriteLine();
                                writer.Write(indentstring);
                            }
                            writer.Write("]");
                        }
                    }
                }
                else if (recurseobjects)
                {
                    var value = p.GetValue(item, null);
                    if (value == null)
                    {
                        if (first)
                            first = false;
                        else
                            writer.WriteLine();
                        writer.Write("{0}{1}: null", indentstring, p.Name);
                    }
                    else if (!visited.ContainsKey(value))
                    {
                        if (first)
                            first = false;
                        else
                            writer.WriteLine();
                        writer.WriteLine("{0}{1}:", indentstring, p.Name);
                        visited[value] = null;
                        PrintSerializeObject(value, writer, filter, recurseobjects, indentation + 4, collectionlimit, visited);
                    }
                }
            }
            writer.Flush();
        }

        /// <summary>
        /// Returns a string representing the object, which can be used for display or logging
        /// </summary>
        /// <returns>The serialized object</returns>
        /// <param name="item">The object to serialize</param>
        /// <param name="filter">A filter applied to properties to decide if they are omitted or not</param>
        /// <param name="recurseobjects">A value indicating if non-primitive values are recursed</param>
        /// <param name="indentation">The string indentation</param>
        /// <param name="collectionlimit">The maximum number of items to report from an IEnumerable instance, set to zero or less for reporting all</param>
        public static StringBuilder PrintSerializeObject(object? item, StringBuilder? sb = null, Func<System.Reflection.PropertyInfo, object, bool>? filter = null, bool recurseobjects = false, int indentation = 0, int collectionlimit = 10)
        {
            sb = sb ?? new StringBuilder();
            using (var sw = new StringWriter(sb))
                PrintSerializeObject(item, sw, filter, recurseobjects, indentation, collectionlimit);
            return sb;
        }

        /// <summary>
        /// Repeatedly hash a value with a salt.
        /// This effectively masks the original value,
        /// and destroys lookup methods, like rainbow tables
        /// </summary>
        /// <param name="data">The data to hash</param>
        /// <param name="salt">The salt to apply</param>
        /// <param name="repeats">The number of times to repeat the hashing</param>
        /// <returns>The salted hash</returns>
        public static byte[] RepeatedHashWithSalt(string data, string salt, int repeats = 1200)
        {
            return RepeatedHashWithSalt(
                Encoding.UTF8.GetBytes(data ?? ""),
                Encoding.UTF8.GetBytes(salt ?? ""),
                repeats);
        }

        /// <summary>
        /// Repeatedly hash a value with a salt.
        /// This effectively masks the original value,
        /// and destroys lookup methods, like rainbow tables
        /// </summary>
        /// <param name="data">The data to hash</param>
        /// <param name="salt">The salt to apply</param>
        /// <returns>The salted hash</returns>
        public static byte[] RepeatedHashWithSalt(byte[] data, byte[] salt, int repeats = 1200)
        {
            // We avoid storing the passphrase directly, 
            // instead we salt and rehash repeatedly
            using (var h = System.Security.Cryptography.SHA256.Create())
            {
                h.Initialize();
                h.TransformBlock(salt, 0, salt.Length, salt, 0);
                h.TransformFinalBlock(data, 0, data.Length);
                var buf = h.Hash ?? throw new CryptographicUnexpectedOperationException("Computed hash was null?");

                for (var i = 0; i < repeats; i++)
                {
                    h.Initialize();
                    h.TransformBlock(salt, 0, salt.Length, salt, 0);
                    h.TransformFinalBlock(buf, 0, buf.Length);
                    buf = h.Hash;
                }

                return buf;
            }
        }

        /// <summary>
        /// Gets the drive letter from the given volume guid.
        /// This method cannot be inlined since the System.Management types are not implemented in Mono
        /// </summary>
        /// <param name="volumeGuid">Volume guid</param>
        /// <returns>Drive letter, as a single character, or null if the volume wasn't found</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        public static string? GetDriveLetterFromVolumeGuid(Guid volumeGuid)
        {
            // Based on this answer:
            // https://stackoverflow.com/questions/10186277/how-to-get-drive-information-by-volume-id
            using (var searcher = new System.Management.ManagementObjectSearcher("Select * from Win32_Volume"))
            {
                string targetId = string.Format(@"\\?\Volume{{{0}}}\", volumeGuid);
                foreach (var obj in searcher.Get())
                {
                    if (string.Equals(obj["DeviceID"].ToString(), targetId, StringComparison.OrdinalIgnoreCase))
                    {
                        object driveLetter = obj["DriveLetter"];
                        if (driveLetter != null)
                        {
                            return obj["DriveLetter"].ToString();
                        }
                        else
                        {
                            // The volume was found, but doesn't have a drive letter associated with it.
                            break;
                        }
                    }
                }

                return null;
            }
        }

        /// <summary>
        /// Gets all volume guids and their associated drive letters.
        /// This method cannot be inlined since the System.Management types are not implemented in Mono
        /// </summary>
        /// <returns>Pairs of drive letter to volume guids</returns>
        [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
        [SupportedOSPlatform("windows")]
        public static IEnumerable<KeyValuePair<string, string>> GetVolumeGuidsAndDriveLetters()
        {
            using (var searcher = new System.Management.ManagementObjectSearcher("Select * from Win32_Volume"))
            {
                foreach (var obj in searcher.Get())
                {
                    var deviceIdObj = obj["DeviceID"];
                    var driveLetterObj = obj["DriveLetter"];
                    if (deviceIdObj != null && driveLetterObj != null)
                    {
                        var deviceId = deviceIdObj.ToString();
                        var driveLetter = driveLetterObj.ToString();
                        if (!string.IsNullOrEmpty(deviceId) && !string.IsNullOrEmpty(driveLetter))
                        {
                            yield return new KeyValuePair<string, string>(driveLetter + @"\", deviceId);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// The regular expression matching all know non-quoted commandline characters
        /// </summary>
        private static readonly Regex COMMANDLINE_SAFE = new Regex(@"[A-Za-z0-9\-_/:\.]*");
        /// <summary>
        /// Special characters that needs to be escaped on Linux
        /// </summary>
        private static readonly Regex COMMANDLINE_ESCAPED_LINUX = new Regex(@"[""$`\\!]");

        /// <summary>
        /// Wraps a single argument in quotes suitable for the passing on the commandline
        /// </summary>
        /// <returns>The wrapped commandline element.</returns>
        /// <param name="arg">The argument to wrap.</param>
        /// <param name="allowEnvExpansion">A flag indicating if environment variables are allowed to be expanded</param>
        [return: NotNullIfNotNull("arg")]
        public static string? WrapCommandLineElement(string? arg, bool allowEnvExpansion)
        {
            if (string.IsNullOrWhiteSpace(arg))
                return arg;

            if (!OperatingSystem.IsWindows())
            {
                // We could consider using single quotes that prevents all expansions
                //if (!allowEnvExpansion)
                //    return "'" + arg.Replace("'", "\\'") + "'";

                // Linux is using backslash to escape, except for !
                arg = COMMANDLINE_ESCAPED_LINUX.Replace(arg, (match) =>
                {
                    if (match.Value == "!")
                        return @"""'!'""";

                    if (match.Value == "$" && allowEnvExpansion)
                        return match.Value;

                    return @"\" + match.Value;
                });
            }
            else
            {
                // Windows needs only needs " replaced with "",
                // but is prone to %var% expansion when used in 
                // immediate mode (i.e. from command prompt)
                // Fortunately it does not expand when processes
                // are started from within .Net

                // TODO: I have not found a way to avoid escaping %varname%,
                // and sadly it expands only if the variable exists
                // making it even rarer and harder to diagnose when
                // it happens
                arg = arg.Replace(@"""", @"""""");

                // Also fix the case where the argument ends with a slash
                if (arg[arg.Length - 1] == '\\')
                    arg += @"\";
            }

            // Check that all characters are in the safe set
            if (COMMANDLINE_SAFE.Match(arg).Length != arg.Length)
                return @"""" + arg + @"""";
            else
                return arg;
        }

        /// <summary>
        /// Wrap a set of commandline arguments suitable for the commandline
        /// </summary>
        /// <returns>A commandline string.</returns>
        /// <param name="args">The arguments to create into a commandline.</param>
        /// <param name="allowEnvExpansion">A flag indicating if environment variables are allowed to be expanded</param>
        public static string WrapAsCommandLine(IEnumerable<string> args, bool allowEnvExpansion = false)
        {
            return string.Join(" ", args.Select(x => WrapCommandLineElement(x, allowEnvExpansion)));
        }

        /// <summary>
        /// Utility method that emulates C#'s built in await keyword without requiring the calling method to be async.
        /// This method should be preferred over using Task.Result, as it doesn't wrap singular exceptions in AggregateExceptions.
        /// (It uses Task.GetAwaiter().GetResult(), which is the same thing that await uses under the covers.)
        /// https://stackoverflow.com/questions/17284517/is-task-result-the-same-as-getawaiter-getresult
        /// </summary>
        /// <param name="task">Task to await</param>
        public static void Await(this Task task)
        {
            task.ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Utility method that emulates C#'s built in await keyword without requiring the calling method to be async.
        /// This method should be preferred over using Task.Result, as it doesn't wrap singular exceptions in AggregateExceptions.
        /// (It uses Task.GetAwaiter().GetResult(), which is the same thing that await uses under the covers.)
        /// https://stackoverflow.com/questions/17284517/is-task-result-the-same-as-getawaiter-getresult
        /// </summary>
        /// <typeparam name="T">Result type</typeparam>
        /// <param name="task">Task to await</param>
        /// <returns>Task result</returns>
        public static T Await<T>(this Task<T> task)
        {
            return task.ConfigureAwait(false).GetAwaiter().GetResult();
        }

        /// <summary>
        /// Utility that computes the delay before the next retry of an operation, optionally using exponential backoff.
        /// Note: when using exponential backoff, the exponent is clamped at 10.
        /// </summary>
        /// <param name="retryDelay">Value of one delay unit</param>
        /// <param name="retryAttempt">The attempt number (e.g. 1 for the first retry, 2 for the second retry, etc.)</param>
        /// <param name="useExponentialBackoff">Whether to use exponential backoff</param>
        /// <returns>The computed delay</returns>
        public static TimeSpan GetRetryDelay(TimeSpan retryDelay, int retryAttempt, bool useExponentialBackoff)
        {
            if (retryAttempt < 1)
            {
                throw new ArgumentException("The attempt number must not be less than 1.", nameof(retryAttempt));
            }

            TimeSpan delay;
            if (useExponentialBackoff)
            {
                var delayTicks = retryDelay.Ticks << Math.Min(retryAttempt - 1, 10);
                delay = TimeSpan.FromTicks(delayTicks);
            }
            else
            {
                delay = retryDelay;
            }

            return delay;
        }

        /// <summary>
        /// Loads the pfxcertificate from bytes into an exportable format.
        /// </summary>
        /// <remarks>This method masks a problem with loading certificates with EC based keys by using a temporary file</remarks>
        /// <param name="pfxcertificate">The certificate as a byte array</param>
        /// <param name="password">The password used to protect the PFX file</param>
        /// <param name="allowUnsafeCertificateLoad">A flag indicating if unsafe certificate loading is allowed</param>
        /// <returns>The loaded certificate</returns>
        public static X509Certificate2Collection LoadPfxCertificate(ReadOnlySpan<byte> pfxcertificate, string? password, bool allowUnsafeCertificateLoad = false)
        {
            if (string.IsNullOrWhiteSpace(password) && !allowUnsafeCertificateLoad)
                throw new ArgumentException("Refusing to write unencryped certificate to disk");

            using var tempfile = new TempFile();
            File.WriteAllBytes(tempfile, pfxcertificate.ToArray());
            return LoadPfxCertificate(tempfile, password);
        }

        /// <summary>
        /// Loads a PFX certificate from a file into an exportable format.
        /// </summary>
        /// <param name="pfxPath">The path to the file</param>
        /// <param name="password">The password used to protect the PFX file</param>
        /// <returns>The loaded certificate</returns>
        public static X509Certificate2Collection LoadPfxCertificate(string pfxPath, string? password)
        {
            if (string.IsNullOrEmpty(pfxPath))
                throw new ArgumentNullException(nameof(pfxPath));

            if (!File.Exists(pfxPath))
                throw new FileNotFoundException("The specified PFX file does not exist.", pfxPath);

            var collection = new X509Certificate2Collection();
            collection.Import(pfxPath, password, X509KeyStorageFlags.Exportable | X509KeyStorageFlags.PersistKeySet);
            return collection;
        }

        /// <summary>
        /// Probes the system for the presence of a loopback address on IPv4
        /// </summary>
        public static bool HasIPv4Loopback =>
            NetworkInterface.GetAllNetworkInterfaces()
                .Where(ni => ni.OperationalStatus == OperationalStatus.Up)
                .SelectMany(ni => ni.GetIPProperties().UnicastAddresses)
                .Any(addr => addr.Address.AddressFamily == AddressFamily.InterNetwork
                             && addr.Address.Equals(IPAddress.Loopback));

        /// <summary>
        /// On systems that have IPV4 and IPV6, the method will return the default loopback ( 127, 0, 0, 1)
        /// On systems with IPV6 only, the method will return the IPV6 loopback (::1)
        /// </summary>
        /// <returns></returns>
        public static string IpVersionCompatibleLoopback =>
            HasIPv4Loopback ? IPAddress.Loopback.ToString() : $"[{IPAddress.IPv6Loopback.ToString()}]";



        /// <summary>
        /// Guesses the URL scheme and returns it
        /// </summary>
        /// <param name="url">The URL to guess the scheme for</param>
        /// <returns>The guessed scheme, or null if no scheme was found</returns>
        public static string? GuessScheme(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
                return null;

            var idx = url.IndexOf("://");
            if (idx < 0 && idx < 15 && idx + "://".Length < url.Length)
                return null;

            return url.Substring(0, idx);
        }

        /// <summary>
        /// Returns a url that is safe to display, by removing any credentials
        /// </summary>
        /// <param name="url">The url to sanitize</param>
        /// <returns>The sanitized url</returns>
        public static string GetUrlWithoutCredentials(string url)
        {
            // Assumed safe part of the url to show
            const int maxShown = 25;
            if (string.IsNullOrWhiteSpace(url))
                return url;

            // Use a reportable url without credentials
            var sepIndex = Math.Max(0, url.IndexOf('|')) + 1;
            var length = url.Length - sepIndex;
            var shown = Math.Min(length, maxShown);
            var hidden = length - maxShown;
            var sanitizedUrl = $"{url[sepIndex..(sepIndex + shown)]}{new string('*', hidden)}";

            // If we can parse it, this result is better
            try
            {
                var uri = new Uri(url[sepIndex..]);
                sanitizedUrl = new Uri($"{uri.Scheme}://{uri.Host}").SetPath(uri.Path).ToString();
            }
            catch
            {
            }

            return sanitizedUrl;
        }

        /// <summary>
        /// Formats the string using the invariant culture
        /// </summary>
        /// <param name="format">The format string</param>
        /// <param name="args">The arguments to format</param>
        /// <returns>The formatted string</returns>
        public static string FormatInvariant(this string format, params object?[] args)
            => string.Format(CultureInfo.InvariantCulture, format, args);

        /// <summary>
        /// Formats the string using the invariant culture
        /// </summary>
        /// <param name="formattable">The formattable string</param>
        /// <returns>The formatted string</returns>
        public static string FormatInvariant(this FormattableString formattable)
            => formattable.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Performs the function with an additional timeout
        /// </summary>
        /// <param name="timeout">The timeout to observe</param>
        /// <param name="token">The cancellation token</param>
        /// <param name="func">The function to invoke</param>
        /// <returns>The task</returns>
        public static async Task WithTimeout(TimeSpan timeout, CancellationToken token, Action<CancellationToken> func)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            try
            {
                await Task.Run(() => func(cts.Token), cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                if (cts.IsCancellationRequested)
                    throw new TimeoutException();
                throw;
            }
        }

        /// <summary>
        /// Performs the function with an additional timeout
        /// </summary>
        /// <param name="timeout">The timeout to observe</param>
        /// <param name="token">The cancellation token</param>
        /// <param name="func">The function to invoke</param>
        /// <returns>The task</returns>
        public static async Task<T> WithTimeout<T>(TimeSpan timeout, CancellationToken token, Func<CancellationToken, T> func)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            try
            {
                return await Task.Run(() => func(cts.Token), cts.Token).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                if (cts.IsCancellationRequested)
                    throw new TimeoutException();
                throw;
            }
        }

        /// <summary>
        /// Performs the function with an additional timeout
        /// </summary>
        /// <param name="timeout">The timeout to observe</param>
        /// <param name="token">The cancellation token</param>
        /// <param name="func">The function to invoke</param>
        /// <returns>The task</returns>
        public static async Task WithTimeout(TimeSpan timeout, CancellationToken token, Func<CancellationToken, Task> func)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            try
            {
                await func(cts.Token);
            }
            catch (TaskCanceledException)
            {
                if (cts.IsCancellationRequested)
                    throw new TimeoutException();
                throw;
            }
        }

        /// <summary>
        /// Performs the function with an additional timeout
        /// </summary>
        /// <typeparam name="T">The return type</typeparam>
        /// <param name="timeout">The timeout to observe</param>
        /// <param name="token">The cancellation token</param>
        /// <param name="func">The function to invoke</param>
        /// <returns>The task</returns>
        public static async Task<T> WithTimeout<T>(TimeSpan timeout, CancellationToken token, Func<CancellationToken, Task<T>> func)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(token);
            cts.CancelAfter(timeout);
            try
            {
                return await func(cts.Token);
            }
            catch (TaskCanceledException)
            {
                if (cts.IsCancellationRequested)
                    throw new TimeoutException();
                throw;
            }
        }

        /// <summary>
        /// Wraps an async enumerable in a timeout observing enumerable
        /// </summary>
        /// <typeparam name="T">The type of the items in the enumerable</typeparam>
        /// <param name="source">The source enumerable</param>
        /// <param name="timeoutPerItem">The timeout to observe for each item</param>
        /// <param name="outerToken">The cancellation token for the outer operation</param>
        /// <returns>The wrapped enumerable</returns>
        public static async IAsyncEnumerable<T> WithPerItemTimeout<T>(
            IAsyncEnumerable<T> source,
            TimeSpan timeoutPerItem,
            [EnumeratorCancellation] CancellationToken outerToken)
        {
            using var timeoutPolicy = new CancellationTokenSource();
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutPolicy.Token, outerToken);

            await using var enumerator = await WithTimeout(timeoutPerItem, outerToken, _ => source.GetAsyncEnumerator(linked.Token)).ConfigureAwait(false);
            while (true)
            {
                timeoutPolicy.CancelAfter(timeoutPerItem);
                Task<bool> moveNextTask;
                try
                {
                    moveNextTask = enumerator.MoveNextAsync().AsTask();
                    var completed = await Task.WhenAny(moveNextTask, Task.Delay(Timeout.Infinite, timeoutPolicy.Token));
                    if (completed != moveNextTask || timeoutPolicy.IsCancellationRequested)
                        throw new TimeoutException($"Timeout while waiting for next item ({timeoutPerItem.TotalSeconds}s)");

                    if (!moveNextTask.Result)
                        break;

                    yield return enumerator.Current;
                }
                finally
                {
                    timeoutPolicy.Token.ThrowIfCancellationRequested();
                    timeoutPolicy.CancelAfter(Timeout.Infinite); // Reset for next item
                }
            }
        }

        /// <summary>
        /// Wraps the stream in a timeout observing stream
        /// </summary>
        /// <param name="stream">The stream to wrap</param>
        /// <param name="timeout">The timeout to observe</param>
        /// <param name="disposeBaseStream">A flag indicating if the base stream should be disposed</param>
        /// <returns>The wrapped stream</returns>
        public static Stream ObserveReadTimeout(this Stream stream, TimeSpan timeout, bool disposeBaseStream = true)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            return new TimeoutObservingStream(stream, disposeBaseStream)
            {
                ReadTimeout = timeout.Ticks > 0 && timeout != Timeout.InfiniteTimeSpan
                    ? (int)timeout.TotalMilliseconds
                    : Timeout.Infinite
            };
        }

        /// <summary>
        /// Wraps the stream in a timeout observing stream
        /// </summary>
        /// <param name="stream">The stream to wrap</param>
        /// <param name="timeout">The timeout to observe</param>
        /// <param name="disposeBaseStream">A flag indicating if the base stream should be disposed</param>
        /// <returns>The wrapped stream</returns>
        public static Stream ObserveWriteTimeout(this Stream stream, TimeSpan timeout, bool disposeBaseStream = true)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            return new TimeoutObservingStream(stream, disposeBaseStream)
            {
                WriteTimeout = timeout.Ticks > 0 && timeout != Timeout.InfiniteTimeSpan
                    ? (int)timeout.TotalMilliseconds
                    : Timeout.Infinite
            };
        }

        /// <summary>
        /// The types of streams that are considered basic (i.e. not wrapped)
        /// </summary>
        private static readonly IReadOnlySet<Type> _basicStreamTypes = new HashSet<Type>
        {
            typeof(FileStream),
            typeof(MemoryStream),
            typeof(NetworkStream),
            typeof(BufferedStream),
            typeof(System.IO.Compression.DeflateStream),
            typeof(System.IO.Compression.GZipStream),
            typeof(System.IO.Compression.ZLibStream)
        };

        /// <summary>
        /// Unwraps a stream from layers of wrapping streams
        /// </summary>
        /// <param name="stream">The stream to unwrap</param>
        /// <returns>The unwrapped stream</returns>
        public static Stream UnwrapThrottledStream(this Stream stream)
        {
            if (stream == null)
                throw new ArgumentNullException(nameof(stream));

            var previousStream = stream;

            do
            {
                previousStream = stream;

                while (stream is WrappingStream wrappingStream)
                    stream = wrappingStream.BaseStream;
                while (stream is OverrideableStream overrideableStream)
                    stream = overrideableStream.BaseStream;

            } while (stream != previousStream);

#if DEBUG
            if (!_basicStreamTypes.Contains(stream.GetType()))
                throw new InvalidOperationException($"The unwrapped stream is not a basic stream, but a {stream.GetType()}");
#endif

            return stream;
        }

        /// <summary>
        /// Calculates the hash of a throttled stream and returns the stream to read
        /// </summary>
        /// <param name="stream">The source stream
        /// <param name="hashalgorithm">The hash algorithm to use</param>
        /// <param name="cancelToken">The cancellation token to observe</param>
        /// <returns>A tuple with the stream, the hash and a temporary file if used</returns>
        public static async Task<(Stream content, string hash, TempFile? tmpfile)> CalculateThrottledStreamHash(Stream stream, string hashalgorithm, CancellationToken cancelToken)
        {
            TempFile? tmp = null;
            string contentHash;
            var measure = stream.UnwrapThrottledStream();
            if (measure.CanSeek)
            {
                var p = measure.Position;

                // Compute the hash
                using (var hashalg = HashFactory.CreateHasher(hashalgorithm))
                    contentHash = ByteArrayAsHexString(hashalg.ComputeHash(measure));

                measure.Position = p;
            }
            else
            {
                // No seeking possible, use a temp file
                tmp = new TempFile();
                await using (var sr = File.OpenWrite(tmp))
                using (var hasher = HashFactory.CreateHasher(hashalgorithm))
                await using (var hc = new HashCalculatingStream(measure, hasher))
                {
                    await CopyStreamAsync(hc, sr, cancelToken).ConfigureAwait(false);
                    contentHash = hc.GetFinalHashString();
                }

                stream = File.OpenRead(tmp);
            }

            return (stream, contentHash, tmp);
        }
    }
}
