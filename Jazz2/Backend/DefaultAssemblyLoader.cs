﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;

namespace Duality.Backend
{
	public class DefaultAssemblyLoader : IAssemblyLoader
	{
		private string execAssemblyDir;

		public event EventHandler<AssemblyResolveEventArgs> AssemblyResolve;
		public event EventHandler<AssemblyLoadedEventArgs> AssemblyLoaded;

		public IEnumerable<string> BaseDirectories
		{
			get
			{
				return new[] { Path.Combine(execAssemblyDir, DualityApp.PluginDirectory) };
			}
		}
		public IEnumerable<string> AvailableAssemblyPaths
		{
			get
			{
				IEnumerable<string> availLibFiles = Enumerable.Empty<string>();
				foreach (string baseDir in this.BaseDirectories)
				{
					availLibFiles = availLibFiles.Concat(Directory.EnumerateFiles(baseDir, "*.dll", SearchOption.TopDirectoryOnly));
				}
				return availLibFiles;
			}
		}
		public IEnumerable<Assembly> LoadedAssemblies
		{
			get { return AppDomain.CurrentDomain.GetAssemblies(); }
		}

		public DefaultAssemblyLoader(string execAssemblyDir)
		{
			this.execAssemblyDir = execAssemblyDir;
		}

		public Assembly LoadAssembly(string assemblyPath)
		{
			// Due to complex dependency resolve situations intertwined with our hot-reloadable
			// plugin system, we should manually resolve all dependencies. This is only possible
			// when obscuring where a certain Assembly has been loaded from. We need to load them
			// all as an anonymous data block to circumvent system dependency resolve.

			// Guess the path of the symbol file
			string pluginDebugInfoPath = Path.Combine(
				Path.GetDirectoryName(assemblyPath), 
				Path.GetFileNameWithoutExtension(assemblyPath)) + ".pdb";
			if (!File.Exists(pluginDebugInfoPath)) {
				pluginDebugInfoPath = null;
			}

			// Load the assembly - and its symbols, if provided
			if (pluginDebugInfoPath != null) {
				return Assembly.Load(File.ReadAllBytes(assemblyPath), File.ReadAllBytes(pluginDebugInfoPath));
			} else {
				return Assembly.Load(File.ReadAllBytes(assemblyPath));
			}
		}
		public int GetAssemblyHash(string assemblyPath)
		{
			if (!File.Exists(assemblyPath)) {
				return 0;
			}

			using (BufferedStream stream = new BufferedStream(File.OpenRead(assemblyPath), 512000))
			{
				var sha = System.Security.Cryptography.MD5.Create();
				byte[] hash = sha.ComputeHash(stream);
				return BitConverter.ToInt32(hash, 0);
			}
		}

		public void Init()
		{
			// Log environment specs for diagnostic purposes
			// Even though more fitted for the system backend, we'll
			// do this here, because the plugin loader available much
			// sooner, and more reliably.
			{
				string osName = Environment.OSVersion != null ? Environment.OSVersion.ToString() : "Unknown";
				string osFriendlyName = null;
				if (Environment.OSVersion.Platform == PlatformID.Win32NT)
				{
					if (Environment.OSVersion.Version >= new Version(10, 0, 0))
						osFriendlyName = "Windows 10";
					else if (Environment.OSVersion.Version >= new Version(6, 3, 0))
						osFriendlyName = "Windows 8.1";
					else if (Environment.OSVersion.Version >= new Version(6, 2, 0))
						osFriendlyName = "Windows 8";
					else if (Environment.OSVersion.Version >= new Version(6, 1, 0))
						osFriendlyName = "Windows 7";
					else if (Environment.OSVersion.Version >= new Version(6, 0, 0))
						osFriendlyName = "Windows Vista";
					else if (Environment.OSVersion.Version >= new Version(5, 2, 0))
						osFriendlyName = "Windows XP 64 Bit Edition";
					else if (Environment.OSVersion.Version >= new Version(5, 1, 0))
						osFriendlyName = "Windows XP";
					else if (Environment.OSVersion.Version >= new Version(5, 0, 0))
						osFriendlyName = "Windows 2000";
				}
				Log.Write(LogType.Verbose,
					"Environment Info: " + Environment.NewLine +
					"  Current Directory: {0}" + Environment.NewLine +
					"  Command Line: {1}" + Environment.NewLine +
					"  Operating System: {2}" + Environment.NewLine +
					"  64 Bit OS: {3}" + Environment.NewLine +
					"  64 Bit Process: {4}" + Environment.NewLine +
					"  CLR Version: {5}" + Environment.NewLine +
					"  Processor Count: {6}",
					Environment.CurrentDirectory,
					Environment.CommandLine,
					osName + (osFriendlyName != null ? (" (" + osFriendlyName + ")") : ""),
					Environment.Is64BitOperatingSystem,
					Environment.Is64BitProcess,
					Environment.Version,
					Environment.ProcessorCount);
			}
			AppDomain.CurrentDomain.AssemblyResolve += this.CurrentDomain_AssemblyResolve;
			AppDomain.CurrentDomain.AssemblyLoad += this.CurrentDomain_AssemblyLoad;
		}
		public void Terminate()
		{
			AppDomain.CurrentDomain.AssemblyResolve -= this.CurrentDomain_AssemblyResolve;
			AppDomain.CurrentDomain.AssemblyLoad -= this.CurrentDomain_AssemblyLoad;
		}

		private Assembly CurrentDomain_AssemblyResolve(object sender, ResolveEventArgs args)
		{
			// First, trigger a resolve event and see if we found a matching Assembly.
			// This will give core and editor plugin managers to load plugin Assemblies
			// their own way, or resolve with an already loaded one.
			if (this.AssemblyResolve != null)
			{
				AssemblyResolveEventArgs resolveArgs = new AssemblyResolveEventArgs(args.Name);
				this.AssemblyResolve(this, resolveArgs);

				if (resolveArgs.IsResolved)
					return resolveArgs.ResolvedAssembly;
			}

			// Admit that we didn't find anything - unless it's a resource Assembly, which
			// is used for WinForms localization. Not finding them is the default / expected.
			bool isResourceAssembly = false;
			if (args.Name != null)
			{
				string token = ".resources";
				int index = args.Name.IndexOf(token);
				int pastEndIndex = index + token.Length;
				if (index != -1 && (pastEndIndex >= args.Name.Length || args.Name[pastEndIndex] == ','))
				{
					isResourceAssembly = true;
				}
			}
			if (!isResourceAssembly)
			{
				if (args.RequestingAssembly != null)
				{
					Log.Write(LogType.Error,
						"Can't resolve Assembly '{0}' (as requested by '{1}'): None of the available assembly paths matches the requested name.",
						args.Name,
						args.RequestingAssembly);
				}
				else
				{
					Log.Write(LogType.Error,
						"Can't resolve Assembly '{0}': None of the available assembly paths matches the requested name.",
						args.Name);
				}
			}
			return null;
		}
		private void CurrentDomain_AssemblyLoad(object sender, AssemblyLoadEventArgs args)
		{
			if (this.AssemblyLoaded != null)
				this.AssemblyLoaded(this, new AssemblyLoadedEventArgs(args.LoadedAssembly));

			Log.Write(LogType.Verbose, "Assembly loaded: {0}", args.LoadedAssembly);
		}
	}
}