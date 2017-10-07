﻿using System;
using System.IO;

namespace Duality.IO
{
    /// <summary>
    /// Defines static methods for performing common file system operations on files.
    /// </summary>
    public static class FileOp
	{
		/// <summary>
		/// Returns whether the specified path refers to an existing file.
		/// </summary>
		/// <param name="path"></param>
		/// <returns></returns>
		public static bool Exists(string path)
		{
			if (string.IsNullOrWhiteSpace(path)) return false;
			PathOp.CheckInvalidPathChars(path);
            return PathOp.ResolveFileSystem(ref path).FileExists(path);
		}
		/// <summary>
		/// Creates or overwrites a file at the specified path and returns a <see cref="System.IO.Stream"/> to it.
		/// The returned stream has implicit read / write access.
		/// </summary>
		/// <param name="path"></param>
		public static Stream Create(string path)
		{
			if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The specified path is null or whitespace-only.");
			PathOp.CheckInvalidPathChars(path);
            return PathOp.ResolveFileSystem(ref path).CreateFile(path);
		}
		/// <summary>
		/// Opens an existing file at the specified path and returns a <see cref="System.IO.Stream"/> to it.
		/// </summary>
		/// <param name="path"></param>
		/// <param name="mode"></param>
		public static Stream Open(string path, FileAccessMode mode)
		{
			if (string.IsNullOrWhiteSpace(path)) throw new ArgumentException("The specified path is null or whitespace-only.");
			PathOp.CheckInvalidPathChars(path);
            return PathOp.ResolveFileSystem(ref path).OpenFile(path, mode);
		}
		/// <summary>
		/// Deletes the file that is referred to by the specified path.
		/// </summary>
		/// <param name="path"></param>
		public static void Delete(string path)
		{
			if (string.IsNullOrWhiteSpace(path)) return;
			PathOp.CheckInvalidPathChars(path);
            PathOp.ResolveFileSystem(ref path).DeleteFile(path);
		}
	}
}