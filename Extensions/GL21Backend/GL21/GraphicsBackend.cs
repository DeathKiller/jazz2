﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using Duality.Drawing;
using Duality.Resources;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL;
using Duality.Backend.DefaultOpenTK;
using System.Runtime.InteropServices;
using Jazz2.Game;
using Jazz2;

namespace Duality.Backend.GL21
{
	public class GraphicsBackend : IGraphicsBackend
	{
		private static readonly Version MinOpenGLVersion = new Version(2, 1);

		private static GraphicsBackend activeInstance = null;
		public static GraphicsBackend ActiveInstance
		{
			get { return activeInstance; }
		}

		private OpenTKGraphicsCapabilities   capabilities = new OpenTKGraphicsCapabilities();
		private IDrawDevice                  currentDevice           = null;
		private RenderOptions                renderOptions           = null;
		private RenderStats                  renderStats             = null;
		private HashSet<GraphicsMode>        availGraphicsModes      = null;
		private GraphicsMode                 defaultGraphicsMode     = null;
		private RawList<uint>                perVertexTypeVBO        = new RawList<uint>();
		private NativeWindow                 activeWindow            = null;
		private Point2                       externalBackbufferSize  = Point2.Zero;
		private bool                         useAlphaToCoverageBlend = false;
		private bool                         msaaIsDriverDisabled    = false;
		private bool                         contextCapsRetrieved    = false;
		private HashSet<NativeShaderProgram> activeShaders           = new HashSet<NativeShaderProgram>();
		private HashSet<string>              sharedShaderParameters  = new HashSet<string>();
		private int                          sharedSamplerBindings   = 0;

		public GraphicsBackendCapabilities Capabilities
		{
			get { return this.capabilities; }
		}
		public GraphicsMode DefaultGraphicsMode
		{
			get { return this.defaultGraphicsMode; }
		}
		public IEnumerable<GraphicsMode> AvailableGraphicsModes
		{
			get { return this.availGraphicsModes; }
		}
		public IEnumerable<ScreenResolution> AvailableScreenResolutions
		{
			get
			{ 
				return DisplayDevice.Default.AvailableResolutions
					.Select(resolution => new ScreenResolution(resolution.Width, resolution.Height, resolution.RefreshRate))
					.Distinct();
			}
		}
		public NativeWindow ActiveWindow
		{
			get { return this.activeWindow; }
		}
		public Point2 ExternalBackbufferSize
		{
			get { return this.externalBackbufferSize; }
			set { this.externalBackbufferSize = value; }
		}

		string IDualityBackend.Id
		{
			get { return "DefaultOpenTKGraphicsBackend"; }
		}
		string IDualityBackend.Name
		{
			get { return "OpenGL 2.1 (OpenTK)"; }
		}
		int IDualityBackend.Priority
		{
			get { return 0; }
		}
		
		bool IDualityBackend.CheckAvailable()
		{
			// Since this is the default backend, it will always try to work.
			return true;
		}
		void IDualityBackend.Init()
		{
			// Initialize OpenTK, if not done yet
			DefaultOpenTKBackendPlugin.InitOpenTK();

			Log.Write(LogType.Info, "Active graphics backend: OpenGL 2.1");

			// Log information about the available display devices
			GraphicsBackend.LogDisplayDevices();

			// Determine available and default graphics modes
			this.QueryGraphicsModes();
			activeInstance = this;
		}
		void IDualityBackend.Shutdown()
		{
			if (activeInstance == this)
				activeInstance = null;

			if (DualityApp.ExecContext != DualityApp.ExecutionContext.Terminated)
			{
				DefaultOpenTKBackendPlugin.GuardSingleThreadState();
				for (int i = 0; i < this.perVertexTypeVBO.Count; i++)
				{
					uint handle = this.perVertexTypeVBO[i];
					if (handle != 0)
					{
						GL.DeleteBuffers(1, ref handle);
					}
				}
				this.perVertexTypeVBO.Clear();
			}

			// Since the window outlives the graphics backend in the usual launcher setup, 
			// we'll need to unhook early, so Duality can complete its cleanup before the window does.
			if (this.activeWindow != null)
			{
				this.activeWindow.UnhookFromDuality();
				this.activeWindow = null;
			}
		}

		void IGraphicsBackend.BeginRendering(IDrawDevice device, RenderOptions options, RenderStats stats)
		{
			DebugCheckOpenGLErrors();
			this.CheckRenderingCapabilities();

			this.currentDevice = device;
			this.renderOptions = options;
			this.renderStats = stats;

			// Prepare the target surface for rendering
			NativeRenderTarget.Bind(options.Target as NativeRenderTarget);

			// Determine whether masked blending should use alpha-to-coverage mode
			if (this.msaaIsDriverDisabled)
				this.useAlphaToCoverageBlend = false;
			else if (NativeRenderTarget.BoundRT != null)
				this.useAlphaToCoverageBlend = NativeRenderTarget.BoundRT.Samples > 0;
			else if (this.activeWindow != null)
				this.useAlphaToCoverageBlend = this.activeWindow.IsMultisampled; 
			else
				this.useAlphaToCoverageBlend = this.defaultGraphicsMode.Samples > 0;

			// Determine the available size on the active rendering surface
			Point2 availableSize;
			if (NativeRenderTarget.BoundRT != null)
				availableSize = new Point2(NativeRenderTarget.BoundRT.Width, NativeRenderTarget.BoundRT.Height);
			else if (this.activeWindow != null)
				availableSize = new Point2(this.activeWindow.Width, this.activeWindow.Height);
			else
				availableSize = this.externalBackbufferSize;

			// Translate viewport coordinates to OpenGL screen coordinates (bottom-left, rising), unless rendering
			// to a texture, which is laid out Duality-like (top-left, descending)
			Rect openGLViewport = options.Viewport;
			if (NativeRenderTarget.BoundRT == null)
			{
				openGLViewport.Y = (availableSize.Y - openGLViewport.H) - openGLViewport.Y;
			}

			// Setup viewport and scissor rects
			GL.Viewport((int)openGLViewport.X, (int)openGLViewport.Y, (int)MathF.Ceiling(openGLViewport.W), (int)MathF.Ceiling(openGLViewport.H));
			GL.Scissor((int)openGLViewport.X, (int)openGLViewport.Y, (int)MathF.Ceiling(openGLViewport.W), (int)MathF.Ceiling(openGLViewport.H));

			// Clear buffers
			ClearBufferMask glClearMask = 0;
			ColorRgba clearColor = options.ClearColor;
			if ((options.ClearFlags & ClearFlag.Color) != ClearFlag.None) glClearMask |= ClearBufferMask.ColorBufferBit;
			if ((options.ClearFlags & ClearFlag.Depth) != ClearFlag.None) glClearMask |= ClearBufferMask.DepthBufferBit;
			GL.ClearColor(clearColor.R / 255.0f, clearColor.G / 255.0f, clearColor.B / 255.0f, clearColor.A / 255.0f);
			GL.ClearDepth((double)options.ClearDepth); // The "float version" is from OpenGL 4.1..
			GL.Clear(glClearMask);

			// Configure Rendering params
			GL.Enable(EnableCap.ScissorTest);
			GL.Enable(EnableCap.DepthTest);
			if (options.DepthTest)
				GL.DepthFunc(DepthFunction.Lequal);
			else
				GL.DepthFunc(DepthFunction.Always);
			
			OpenTK.Matrix4 openTkView;
			Matrix4 view = options.ViewMatrix;
			GetOpenTKMatrix(ref view, out openTkView);

			GL.MatrixMode(MatrixMode.Modelview);
			GL.LoadMatrix(ref openTkView);

			Matrix4 projectionMatrix = options.ProjectionMatrix;
			if (NativeRenderTarget.BoundRT != null) {
				Matrix4 flipOutput = Matrix4.CreateScale(1.0f, -1.0f, 1.0f);
				projectionMatrix = projectionMatrix * flipOutput;
			}

			OpenTK.Matrix4 openTkProjection;
			Matrix4 projection = projectionMatrix;
			GetOpenTKMatrix(ref projection, out openTkProjection);

			GL.MatrixMode(MatrixMode.Projection);
			GL.LoadMatrix(ref openTkProjection);
		}
		void IGraphicsBackend.Render(IReadOnlyList<DrawBatch> batches)
		{
			if (batches.Count == 0) return;

			this.RetrieveActiveShaders(batches);
			this.SetupSharedParameters(this.renderOptions.ShaderParameters);

			int drawCalls = 0;
			DrawBatch lastRendered = null;
			for (int i = 0; i < batches.Count; i++) {
				DrawBatch batch = batches[i];
				VertexDeclaration vertexType = batch.VertexBuffer.VertexType;

				// Bind the vertex buffer we'll use. Note that this needs to be done
				// before setting up any vertex format state.
				NativeGraphicsBuffer vertexBuffer = batch.VertexBuffer.NativeVertex as NativeGraphicsBuffer;
				NativeGraphicsBuffer.Bind(GraphicsBufferType.Vertex, vertexBuffer);

				bool first = (i == 0);
				bool sameMaterial =
					lastRendered != null &&
					lastRendered.Material.Equals(batch.Material);

				// Setup vertex bindings. Note that the setup differs based on the 
				// materials shader, so material changes can be vertex binding changes.
				if (lastRendered != null) {
					this.FinishVertexFormat(lastRendered.Material, lastRendered.VertexBuffer.VertexType);
				}
				this.SetupVertexFormat(batch.Material, vertexType);

				// Setup material when changed.
				if (!sameMaterial) {
					this.SetupMaterial(
						batch.Material,
						lastRendered != null ? lastRendered.Material : null);
				}

				// Draw the current batch
				this.DrawVertexBatch(
					batch.VertexBuffer,
					batch.VertexRanges,
					batch.VertexMode);

				drawCalls++;
				lastRendered = batch;
			}

			// Cleanup after rendering
			NativeGraphicsBuffer.Bind(GraphicsBufferType.Vertex, null);
			NativeGraphicsBuffer.Bind(GraphicsBufferType.Index, null);
			if (lastRendered != null) {
				this.FinishMaterial(lastRendered.Material);
				this.FinishVertexFormat(lastRendered.Material, lastRendered.VertexBuffer.VertexType);
			}

			if (this.renderStats != null) {
				this.renderStats.DrawCalls += drawCalls;
			}

			this.FinishSharedParameters();
		}
		void IGraphicsBackend.EndRendering()
		{
			GL.BindBuffer(BufferTarget.ArrayBuffer, 0);

			this.currentDevice = null;
			this.renderOptions = null;
			this.renderStats = null;

			DebugCheckOpenGLErrors();
		}
		
		INativeGraphicsBuffer IGraphicsBackend.CreateBuffer(GraphicsBufferType type)
		{
			return new NativeGraphicsBuffer(type);
		}
		INativeTexture IGraphicsBackend.CreateTexture()
		{
			return new NativeTexture();
		}
		INativeRenderTarget IGraphicsBackend.CreateRenderTarget()
		{
			return new NativeRenderTarget();
		}
		INativeShaderPart IGraphicsBackend.CreateShaderPart()
		{
			return new NativeShaderPart();
		}
		INativeShaderProgram IGraphicsBackend.CreateShaderProgram()
		{
			return new NativeShaderProgram();
		}
		INativeWindow IGraphicsBackend.CreateWindow(WindowOptions options)
		{
			// Only one game window allowed at a time
			if (this.activeWindow != null)
			{
				(this.activeWindow as INativeWindow).Dispose();
				this.activeWindow = null;
			}

			// Create a window and keep track of it
			this.activeWindow = new NativeWindow(defaultGraphicsMode, options);
			return this.activeWindow;
		}

		void IGraphicsBackend.GetOutputPixelData(IntPtr buffer, ColorDataLayout dataLayout, ColorDataElementType dataElementType, int x, int y, int width, int height)
		{
			DefaultOpenTKBackendPlugin.GuardSingleThreadState();

			NativeRenderTarget lastRt = NativeRenderTarget.BoundRT;
			NativeRenderTarget.Bind(null);
			{
				// Use a temporary local buffer, since the image will be upside-down because
				// of OpenGL's coordinate system and we'll need to flip it before returning.
				byte[] byteData = new byte[width * height * 4];
				
				// Retrieve pixel data
				GL.ReadPixels(x, y, width, height, dataLayout.ToOpenTK(), dataElementType.ToOpenTK(), byteData);
				
				// Flip the retrieved image vertically
				int bytesPerLine = width * 4;
				byte[] switchLine = new byte[width * 4];
				for (int flipY = 0; flipY < height / 2; flipY++)
				{
					int lineIndex = flipY * width * 4;
					int lineIndex2 = (height - 1 - flipY) * width * 4;
					
					// Copy the current line to the switch buffer
					for (int lineX = 0; lineX < bytesPerLine; lineX++)
					{
						switchLine[lineX] = byteData[lineIndex + lineX];
					}

					// Copy the opposite line to the current line
					for (int lineX = 0; lineX < bytesPerLine; lineX++)
					{
						byteData[lineIndex + lineX] = byteData[lineIndex2 + lineX];
					}

					// Copy the switch buffer to the opposite line
					for (int lineX = 0; lineX < bytesPerLine; lineX++)
					{
						byteData[lineIndex2 + lineX] = switchLine[lineX];
					}
				}
				
				// Copy the flipped data to the output buffer
				Marshal.Copy(byteData, 0, buffer, width * height * 4);
			}
			NativeRenderTarget.Bind(lastRt);
		}

		public void QueryOpenGLCapabilities()
		{
			// Retrieve and log GL version as well as detected capabilities and limits
			this.capabilities.RetrieveFromAPI();
			this.capabilities.WriteToLog();

			// Log a warning if the detected GL version is below our supported minspec
			Version glVersion = this.capabilities.GLVersion;
			if (glVersion < MinOpenGLVersion) {
				Log.Write(LogType.Warning,
					"The detected OpenGL version {0} appears to be lower than the required minimum. Version {1} or higher is required to run Duality applications.",
					glVersion,
					MinOpenGLVersion);
			}
		}
		private void QueryGraphicsModes()
		{
			int[] aaLevels = new int[] { 0, 2, 4, 6, 8, 16 };
			this.availGraphicsModes = new HashSet<GraphicsMode>(new GraphicsModeComparer());
			foreach (int samplecount in aaLevels)
			{
				GraphicsMode mode = new GraphicsMode(32, 24, 0, samplecount, new OpenTK.Graphics.ColorFormat(0), 2, false);
				if (!this.availGraphicsModes.Contains(mode)) this.availGraphicsModes.Add(mode);
			}
			int highestAALevel = MathF.RoundToInt(MathF.Log(MathF.Max(this.availGraphicsModes.Max(m => m.Samples), 1.0f), 2.0f));
			int targetAALevel = highestAALevel;
			/*if (DualityApp.AppData.MultisampleBackBuffer)
			{
				switch (DualityApp.UserData.AntialiasingQuality)
				{
					case AAQuality.High:	targetAALevel = highestAALevel;		break;
					case AAQuality.Medium:	targetAALevel = highestAALevel / 2; break;
					case AAQuality.Low:		targetAALevel = highestAALevel / 4; break;
					case AAQuality.Off:		targetAALevel = 0;					break;
				}
			}
			else
			{*/
				targetAALevel = 0;
			/*}*/
			int targetSampleCount = MathF.RoundToInt(MathF.Pow(2.0f, targetAALevel));
			this.defaultGraphicsMode = this.availGraphicsModes.LastOrDefault(m => m.Samples <= targetSampleCount) ?? this.availGraphicsModes.Last();
		}
		private void CheckRenderingCapabilities()
		{
			if (this.contextCapsRetrieved) return;
			this.contextCapsRetrieved = true;

			//App.Log("Determining OpenGL rendering capabilities...");
			//Logs.Core.PushIndent();

			// Make sure we're not on a render target, which may override
			// some settings that we'd like to get from the main contexts
			// backbuffer.
			NativeRenderTarget oldTarget = NativeRenderTarget.BoundRT;
			NativeRenderTarget.Bind(null);

			int targetSamples = this.defaultGraphicsMode.Samples;
			int actualSamples;

			// Retrieve how many MSAA samples are actually available, despite what 
			// was offered and requested vis graphics mode.
			CheckOpenGLErrors(true);
			actualSamples = GL.GetInteger(GetPName.Samples);
			if (CheckOpenGLErrors()) actualSamples = targetSamples;

			// If the sample count differs, mention it in the logs. If it is
			// actually zero, assume MSAA is driver-disabled.
			if (targetSamples != actualSamples)
			{
				Log.Write(LogType.Warning, "Requested {0} MSAA samples, but got {1} samples instead.", targetSamples, actualSamples);
				if (actualSamples == 0)
				{
					this.msaaIsDriverDisabled = true;
					Log.Write(LogType.Warning, "Assuming MSAA is unavailable. Duality will not use Alpha-to-Coverage masking techniques.");
				}
			}

			NativeRenderTarget.Bind(oldTarget);

			//Logs.Core.PopIndent();
		}

		/// <summary>
		/// Updates the internal list of active shaders based on the specified rendering batches.
		/// </summary>
		/// <param name="batches"></param>
		private void RetrieveActiveShaders(IReadOnlyList<DrawBatch> batches)
		{
			this.activeShaders.Clear();
			for (int i = 0; i < batches.Count; i++)
			{
				DrawBatch batch = batches[i];
				BatchInfo material = batch.Material;
				DrawTechnique tech = material.Technique.Res ?? DrawTechnique.Solid.Res;
				this.activeShaders.Add(tech.NativeShader as NativeShaderProgram);
			}
		}
		/// <summary>
		/// Applies the specified parameter values to all currently active shaders.
		/// </summary>
		/// <param name="sharedParams"></param>
		/// <seealso cref="RetrieveActiveShaders"/>
		private void SetupSharedParameters(ShaderParameterCollection sharedParams)
		{
			this.sharedSamplerBindings = 0;
			this.sharedShaderParameters.Clear();
			if (sharedParams == null) return;

			foreach (NativeShaderProgram shader in this.activeShaders)
			{
				NativeShaderProgram.Bind(shader);

				ShaderFieldInfo[] varInfo = shader.Fields;
				int[] locations = shader.FieldLocations;

				// Setup shared sampler bindings and uniform data
				for (int i = 0; i < varInfo.Length; i++) {
					ref ShaderFieldInfo field = ref varInfo[i];

					if (field.Scope == ShaderFieldScope.Attribute) continue;
					if (field.Type == ShaderFieldType.Sampler2D) {
						ContentRef<Texture> texRef;
						if (!sharedParams.TryGetInternal(field.Name, out texRef)) continue;

						NativeTexture.Bind(texRef, this.sharedSamplerBindings);
						GL.Uniform1(locations[i], this.sharedSamplerBindings);

						this.sharedSamplerBindings++;
					} else {
						float[] data;
						if (!sharedParams.TryGetInternal(field.Name, out data)) continue;

						NativeShaderProgram.SetUniform(ref field, locations[i], data);
					}

					this.sharedShaderParameters.Add(field.Name);
				}
			}

			NativeShaderProgram.Bind(null);
		}
		private void SetupVertexFormat(BatchInfo material, VertexDeclaration vertexDeclaration)
		{
			DrawTechnique technique = material.Technique.Res ?? DrawTechnique.Solid.Res;
			NativeShaderProgram nativeProgram = (technique.NativeShader ?? DrawTechnique.Solid.Res.NativeShader) as NativeShaderProgram;

			VertexElement[] elements = vertexDeclaration.Elements;

			for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
			{
				switch (elements[elementIndex].Role)
				{
					case VertexElementRole.Position:
					{
						GL.EnableClientState(ArrayCap.VertexArray);
						GL.VertexPointer(
							elements[elementIndex].Count, 
							VertexPointerType.Float, 
							vertexDeclaration.Size, 
							elements[elementIndex].Offset);
						break;
					}
					case VertexElementRole.TexCoord:
					{
						GL.EnableClientState(ArrayCap.TextureCoordArray);
						GL.TexCoordPointer(
							elements[elementIndex].Count, 
							TexCoordPointerType.Float, 
							vertexDeclaration.Size, 
							elements[elementIndex].Offset);
						break;
					}
					case VertexElementRole.Color:
					{
						ColorPointerType attribType;
						switch (elements[elementIndex].Type)
						{
							default:
							case VertexElementType.Float: attribType = ColorPointerType.Float; break;
							case VertexElementType.Byte: attribType = ColorPointerType.UnsignedByte; break;
						}

						GL.EnableClientState(ArrayCap.ColorArray);
						GL.ColorPointer(
							elements[elementIndex].Count, 
							attribType, 
							vertexDeclaration.Size, 
							elements[elementIndex].Offset);
						break;
					}
					default:
					{
						if (nativeProgram != null)
						{
							ShaderFieldInfo[] varInfo = nativeProgram.Fields;
							int[] locations = nativeProgram.FieldLocations;

							int selectedVar = -1;
							for (int varIndex = 0; varIndex < varInfo.Length; varIndex++)
							{
								if (locations[varIndex] == -1) continue;
								if (!ShaderVarMatches(
									ref varInfo[varIndex],
									elements[elementIndex].Type, 
									elements[elementIndex].Count))
									continue;
								
								selectedVar = varIndex;
								break;
							}
							if (selectedVar == -1) break;

							VertexAttribPointerType attribType;
							switch (elements[elementIndex].Type)
							{
								default:
								case VertexElementType.Float: attribType = VertexAttribPointerType.Float; break;
								case VertexElementType.Byte: attribType = VertexAttribPointerType.UnsignedByte; break;
							}

							GL.EnableVertexAttribArray(locations[selectedVar]);
							GL.VertexAttribPointer(
								locations[selectedVar], 
								elements[elementIndex].Count, 
								attribType, 
								false, 
								vertexDeclaration.Size, 
								elements[elementIndex].Offset);
						}
						break;
					}
				}
			}
		}
		private void SetupMaterial(BatchInfo material, BatchInfo lastMaterial)
		{
			DrawTechnique tech = material.Technique.Res ?? DrawTechnique.Solid.Res;
			DrawTechnique lastTech = lastMaterial != null ? lastMaterial.Technique.Res : null;
			
			// Setup BlendType
			if (lastTech == null || tech.Blending != lastTech.Blending)
				this.SetupBlendType(tech.Blending);

			// Bind Shader
			NativeShaderProgram nativeShader = tech.NativeShader as NativeShaderProgram;
			NativeShaderProgram.Bind(nativeShader);

			// Setup shader data
			ShaderFieldInfo[] varInfo = nativeShader.Fields;
			int[] locations = nativeShader.FieldLocations;

			// Setup sampler bindings and uniform data
			int curSamplerIndex = this.sharedSamplerBindings;
			for (int i = 0; i < varInfo.Length; i++)
			{
				ref ShaderFieldInfo field = ref varInfo[i];

				if (field.Scope == ShaderFieldScope.Attribute) continue;
				if (this.sharedShaderParameters.Contains(field.Name)) continue;

				if (field.Type == ShaderFieldType.Sampler2D)
				{
					ContentRef<Texture> texRef = material.GetInternalTexture(field.Name);
					NativeTexture.Bind(texRef, curSamplerIndex);
					GL.Uniform1(locations[i], curSamplerIndex);

					curSamplerIndex++;
				}
				else
				{
					float[] data = material.GetInternalData(field.Name);
					if (data == null)
						continue;

					NativeShaderProgram.SetUniform(ref varInfo[i], locations[i], data);
				}
			}
			NativeTexture.ResetBinding(curSamplerIndex);
		}
		private void SetupBlendType(BlendMode mode, bool depthWrite = true)
		{
			switch (mode)
			{
				default:
				case BlendMode.Reset:
				case BlendMode.Solid:
					GL.DepthMask(depthWrite);
					GL.Disable(EnableCap.Blend);
					GL.Disable(EnableCap.AlphaTest);
					GL.Disable(EnableCap.SampleAlphaToCoverage);
					break;
				case BlendMode.Mask:
					GL.DepthMask(depthWrite);
					GL.Disable(EnableCap.Blend);
					if (this.useAlphaToCoverageBlend)
					{
						GL.Disable(EnableCap.AlphaTest);
						GL.Enable(EnableCap.SampleAlphaToCoverage);
					}
					else
					{
						GL.Enable(EnableCap.AlphaTest);
						GL.AlphaFunc(AlphaFunction.Gequal, 0.5f);
					}
					break;
				case BlendMode.Alpha:
					GL.DepthMask(false);
					GL.Enable(EnableCap.Blend);
					GL.Disable(EnableCap.AlphaTest);
					GL.Disable(EnableCap.SampleAlphaToCoverage);
					GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
					break;
				case BlendMode.AlphaPre:
					GL.DepthMask(false);
					GL.Enable(EnableCap.Blend);
					GL.Disable(EnableCap.AlphaTest);
					GL.Disable(EnableCap.SampleAlphaToCoverage);
					GL.BlendFuncSeparate(BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha, BlendingFactorSrc.One, BlendingFactorDest.OneMinusSrcAlpha);
					break;
				case BlendMode.Add:
					GL.DepthMask(false);
					GL.Enable(EnableCap.Blend);
					GL.Disable(EnableCap.AlphaTest);
					GL.Disable(EnableCap.SampleAlphaToCoverage);
					GL.BlendFuncSeparate(BlendingFactorSrc.SrcAlpha, BlendingFactorDest.One, BlendingFactorSrc.One, BlendingFactorDest.One);
					break;
				case BlendMode.Light:
					GL.DepthMask(false);
					GL.Enable(EnableCap.Blend);
					GL.Disable(EnableCap.AlphaTest);
					GL.Disable(EnableCap.SampleAlphaToCoverage);
					GL.BlendFuncSeparate(BlendingFactorSrc.DstColor, BlendingFactorDest.One, BlendingFactorSrc.Zero, BlendingFactorDest.One);
					break;
				case BlendMode.Multiply:
					GL.DepthMask(false);
					GL.Enable(EnableCap.Blend);
					GL.Disable(EnableCap.AlphaTest);
					GL.Disable(EnableCap.SampleAlphaToCoverage);
					GL.BlendFunc(BlendingFactorSrc.DstColor, BlendingFactorDest.Zero);
					break;
				case BlendMode.Invert:
					GL.DepthMask(false);
					GL.Enable(EnableCap.Blend);
					GL.Disable(EnableCap.AlphaTest);
					GL.Disable(EnableCap.SampleAlphaToCoverage);
					GL.BlendFunc(BlendingFactorSrc.OneMinusDstColor, BlendingFactorDest.OneMinusSrcColor);
					break;
			}
		}

		/// <summary>
		/// Draws the vertices of a single <see cref="DrawBatch"/>, after all other rendering state
		/// has been set up accordingly outside this method.
		/// </summary>
		/// <param name="buffer"></param>
		/// <param name="ranges"></param>
		/// <param name="mode"></param>
		private void DrawVertexBatch(VertexBuffer buffer, RawList<VertexDrawRange> ranges, VertexMode mode)
		{
			NativeGraphicsBuffer indexBuffer = (buffer.IndexCount > 0 ? buffer.NativeIndex : null) as NativeGraphicsBuffer;
			IndexDataElementType indexType = buffer.IndexType;

			// Rendering using index buffer
			if (indexBuffer != null) {
				if (ranges != null && ranges.Count > 0) {
					Log.Write(LogType.Warning,
						"Rendering {0} instances that use index buffers do not support specifying vertex ranges, " +
						"since the two features are mutually exclusive.",
						typeof(DrawBatch).Name,
						typeof(VertexMode).Name);
				}

				NativeGraphicsBuffer.Bind(GraphicsBufferType.Index, indexBuffer);

				PrimitiveType openTkMode = GetOpenTKVertexMode(mode);
				DrawElementsType openTkIndexType = GetOpenTKIndexType(indexType);
				GL.DrawElements(
					openTkMode,
					buffer.IndexCount,
					openTkIndexType,
					IntPtr.Zero);
			}
			// Rendering using an array of vertex ranges
			else {
				NativeGraphicsBuffer.Bind(GraphicsBufferType.Index, null);

				PrimitiveType openTkMode = GetOpenTKVertexMode(mode);
				VertexDrawRange[] rangeData = ranges.Data;
				int rangeCount = ranges.Count;
				for (int r = 0; r < rangeCount; r++) {
					GL.DrawArrays(
						openTkMode,
						rangeData[r].Index,
						rangeData[r].Count);
				}
			}
		}

		private void FinishSharedParameters()
		{
			NativeTexture.ResetBinding();

			this.sharedSamplerBindings = 0;
			this.sharedShaderParameters.Clear();
			this.activeShaders.Clear();
		}
		private void FinishVertexFormat(BatchInfo material, VertexDeclaration vertexDeclaration)
		{
			DrawTechnique technique = material.Technique.Res ?? DrawTechnique.Solid.Res;
			NativeShaderProgram nativeProgram = (technique.NativeShader ?? DrawTechnique.Solid.Res.NativeShader) as NativeShaderProgram;

			VertexElement[] elements = vertexDeclaration.Elements;
			for (int elementIndex = 0; elementIndex < elements.Length; elementIndex++)
			{
				switch (elements[elementIndex].Role)
				{
					case VertexElementRole.Position:
					{
						GL.DisableClientState(ArrayCap.VertexArray);
						break;
					}
					case VertexElementRole.TexCoord:
					{
						GL.DisableClientState(ArrayCap.TextureCoordArray);
						break;
					}
					case VertexElementRole.Color:
					{
						GL.DisableClientState(ArrayCap.ColorArray);
						break;
					}
					default:
					{
						if (nativeProgram != null)
						{
							ShaderFieldInfo[] varInfo = nativeProgram.Fields;
							int[] locations = nativeProgram.FieldLocations;

							int selectedVar = -1;
							for (int varIndex = 0; varIndex < varInfo.Length; varIndex++)
							{
								if (locations[varIndex] == -1) continue;
								if (!ShaderVarMatches(
									ref varInfo[varIndex],
									elements[elementIndex].Type, 
									elements[elementIndex].Count))
									continue;
								
								selectedVar = varIndex;
								break;
							}
							if (selectedVar == -1) break;

							GL.DisableVertexAttribArray(locations[selectedVar]);
						}
						break;
					}
				}
			}
		}
		private void FinishMaterial(BatchInfo material)
		{
			//DrawTechnique tech = material.Technique.Res;
			this.SetupBlendType(BlendMode.Reset);
			NativeShaderProgram.Bind(null);
			NativeTexture.ResetBinding(this.sharedSamplerBindings);
		}
		
		private static PrimitiveType GetOpenTKVertexMode(VertexMode mode)
		{
			switch (mode)
			{
				default:
				case VertexMode.Points:			return PrimitiveType.Points;
				case VertexMode.Lines:			return PrimitiveType.Lines;
				case VertexMode.LineStrip:		return PrimitiveType.LineStrip;
				case VertexMode.LineLoop:		return PrimitiveType.LineLoop;
				case VertexMode.Triangles:		return PrimitiveType.Triangles;
				case VertexMode.TriangleStrip:	return PrimitiveType.TriangleStrip;
				case VertexMode.TriangleFan:	return PrimitiveType.TriangleFan;
				case VertexMode.Quads:			return PrimitiveType.Quads;
			}
		}
		private static DrawElementsType GetOpenTKIndexType(IndexDataElementType indexType)
		{
			switch (indexType) {
				default:
				case IndexDataElementType.UnsignedByte: return DrawElementsType.UnsignedByte;
				case IndexDataElementType.UnsignedShort: return DrawElementsType.UnsignedShort;
			}
		}
		private static void GetOpenTKMatrix(ref Matrix4 source, out OpenTK.Matrix4 target)
		{
			target = new OpenTK.Matrix4(
				source.M11, source.M12, source.M13, source.M14,
				source.M21, source.M22, source.M23, source.M24,
				source.M31, source.M32, source.M33, source.M34,
				source.M41, source.M42, source.M43, source.M44);
		}
		private static bool ShaderVarMatches(ref ShaderFieldInfo varInfo, VertexElementType type, int count)
		{
			if (varInfo.Scope != ShaderFieldScope.Attribute) return false;

			Type elementPrimitive = varInfo.Type.GetElementPrimitive();
			Type requiredPrimitive = null;
			switch (type)
			{
				case VertexElementType.Byte:
					requiredPrimitive = typeof(byte);
					break;
				case VertexElementType.Float:
					requiredPrimitive = typeof(float);
					break;
			}
			if (elementPrimitive != requiredPrimitive)
				return false;

			int elementCount = varInfo.Type.GetElementCount();
			if (count != elementCount * varInfo.ArrayLength)
				return false;

			return true;
		}

		public static void LogDisplayDevices()
		{
			Log.Write(LogType.Info, "Available display devices:");
			//Logs.Core.PushIndent();
			foreach (DisplayIndex index in new[] { DisplayIndex.First, DisplayIndex.Second, DisplayIndex.Third, DisplayIndex.Fourth, DisplayIndex.Sixth } )
			{
				DisplayDevice display = DisplayDevice.GetDisplay(index);
				if (display == null) continue;

				Log.Write(LogType.Verbose,
					"{0,-6}: {1,4}x{2,4} at {3,3} Hz, {4,2} bpp, pos [{5,4},{6,4}]{7}",
					index,
					display.Width,
					display.Height,
					display.RefreshRate,
					display.BitsPerPixel,
					display.Bounds.X,
					display.Bounds.Y,
					display.IsPrimary ? " (Primary)" : "");
			}
			//Logs.Core.PopIndent();
		}
		/// <summary>
		/// Checks for errors that might have occurred during video processing. You should avoid calling this method due to performance reasons.
		/// Only use it on suspect.
		/// </summary>
		/// <param name="silent">If true, errors aren't logged.</param>
		/// <returns>True, if an error occurred, false if not.</returns>
		public static bool CheckOpenGLErrors(bool silent = false, [CallerMemberName] string callerInfoMember = null, [CallerFilePath] string callerInfoFile = null, [CallerLineNumber] int callerInfoLine = -1)
		{
			// Accessing OpenGL functionality requires context. Don't get confused by AccessViolationExceptions, fail better instead.
			GraphicsContext.Assert();

			ErrorCode error;
			bool found = false;
			while ((error = GL.GetError()) != ErrorCode.NoError)
			{
				if (!silent)
				{
					Log.Write(LogType.Error,
						"Internal OpenGL error, code {0} at {1} in {2}, line {3}.", 
						error,
						callerInfoMember,
						callerInfoFile,
						callerInfoLine);
				}
				found = true;
			}
			if (found && !silent && System.Diagnostics.Debugger.IsAttached) System.Diagnostics.Debugger.Break();
			return found;
		}
		/// <summary>
		/// Checks for OpenGL errors using <see cref="CheckOpenGLErrors"/> when both compiled in debug mode and a with an attached debugger.
		/// </summary>
		/// <returns></returns>
		[System.Diagnostics.Conditional("DEBUG")]
		public static void DebugCheckOpenGLErrors([CallerMemberName] string callerInfoMember = null, [CallerFilePath] string callerInfoFile = null, [CallerLineNumber] int callerInfoLine = -1)
		{
			if (!System.Diagnostics.Debugger.IsAttached) return;
			CheckOpenGLErrors(false, callerInfoMember, callerInfoFile, callerInfoLine);
		}
	}
}