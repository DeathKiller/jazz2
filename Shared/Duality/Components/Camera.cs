﻿using System.Collections.Generic;
using System.Linq;
using Duality.Drawing;
using Duality.Resources;

namespace Duality.Components
{
    /// <summary>
    /// A Camera is responsible for rendering the current <see cref="Duality.Resources.Scene"/>.
    /// </summary>
    [RequiredComponent(typeof(Transform))]
	public sealed class Camera : Component, ICmpInitializable
	{
		private float                     nearZ            = 0.0f;
		private float                     farZ             = 10000.0f;
		private float                     focusDist        = DrawDevice.DefaultFocusDist;
		private Rect                      targetRect       = new Rect(1.0f, 1.0f);
		private PerspectiveMode           perspective      = PerspectiveMode.Parallax;
		private VisibilityFlag            visibilityMask   = VisibilityFlag.All;
		private ColorRgba                 clearColor       = ColorRgba.TransparentBlack;
		private ContentRef<RenderTarget>  renderTarget     = null;
		private ContentRef<RenderSetup>   renderSetup      = null;
		private int                       priority         = 0;
		private ShaderParameterCollection shaderParameters = new ShaderParameterCollection();

		private DrawDevice         drawDevice   = null;
		private PickingRenderSetup pickingSetup = null;
		

		/// <summary>
		/// [GET / SET] The lowest Z value that can be displayed by the device.
		/// </summary>
		public float NearZ
		{
			get { return this.nearZ; }
			set { this.nearZ = value; }
		}
		/// <summary>
		/// [GET / SET] The highest Z value that can be displayed by the device.
		/// </summary>
		public float FarZ
		{
			get { return this.farZ; }
			set { this.farZ = value; }
		}
		/// <summary>
		/// [GET / SET] Reference distance for calculating the view projection. When using <see cref="PerspectiveMode.Parallax"/>, 
		/// an object this far away from the Camera will always appear in its original size and without offset.
		/// </summary>
		public float FocusDist
		{
			get { return this.focusDist; }
			set { this.focusDist = MathF.Max(value, 0.01f); }
		}
		/// <summary>
		/// [GET / SET] The rectangular area this camera will render into, relative to the
		/// total available viewport during rendering.
		/// </summary>
		public Rect TargetRect
		{
			get { return this.targetRect; }
			set
			{
				Rect intersection = value.Intersection(new Rect(1.0f, 1.0f));
				if (intersection == Rect.Empty) return;
				this.targetRect = intersection;
			}
		}
		/// <summary>
		/// [GET / SET] Specifies the perspective effect that is applied when rendering the world.
		/// </summary>
		public PerspectiveMode Perspective
		{
			get { return this.perspective; }
			set { this.perspective = value; }
		}
		/// <summary>
		/// [GET / SET] A bitmask flagging all visibility groups that are considered visible to this drawing device.
		/// </summary>
		public VisibilityFlag VisibilityMask
		{
			get { return this.visibilityMask; }
			set { this.visibilityMask = value; }
		}
		/// <summary>
		/// [GET / SET] The default background color of the rendered image.
		/// </summary>
		public ColorRgba ClearColor
		{
			get { return this.clearColor; }
			set { this.clearColor = value; }
		}
		/// <summary>
		/// [GET / SET] When set, the camera will render all output that would normally end up
		/// on screen to the specified <see cref="RenderTarget"/> instead.
		/// </summary>
		public ContentRef<RenderTarget> Target
		{
			get { return this.renderTarget; }
			set { this.renderTarget = value; }
		}
		/// <summary>
		/// [GET / SET] The <see cref="RenderSetup"/> that should be used by this camera. Will
		/// fall back to the application-default <see cref="DualityAppData.RenderingSetup"/> when unavailable.
		/// </summary>
		public ContentRef<RenderSetup> RenderingSetup
		{
			get { return this.renderSetup; }
			set { this.renderSetup = value; }
		}
		/// <summary>
		/// [GET] The rendering setup that will be used by this camera.
		/// </summary>
		public RenderSetup ActiveRenderSetup
		{
			get
			{
				return 
					this.renderSetup.Res ?? 
					//DualityApp.AppData.RenderingSetup.Res ?? 
					RenderSetup.Default.Res;
			}
		}
		/// <summary>
		/// [GET / SET] Cameras with higher priority values render first.
		/// </summary>
		public int Priority
		{
			get { return this.priority; }
			set { this.priority = value; }
		}
		/// <summary>
		/// [GET] Provides access to the cameras shared <see cref="ShaderParameterCollection"/>,
		/// which allows to specify a parameter value globally across all materials rendered by
		/// this <see cref="Camera"/>.
		/// </summary>
		public ShaderParameterCollection ShaderParameters
		{
			get { return this.shaderParameters; }
		}


		/// <summary>
		/// Renders the current <see cref="Duality.Resources.Scene"/>.
		/// </summary>
		/// <param name="viewportRect">The viewport area to which will be rendered.</param>
		/// <param name="imageSize">Target size of the rendered image before adjusting it to fit the specified viewport.</param>
		public void Render(Rect viewportRect, Vector2 imageSize)
		{
			// Configure the wrapped drawing device, so rendering matrices and settings
			// are set up properly.
			this.UpdateDeviceConfig();

			// Adjust the local render size and viewport according to the camera target rect
			Vector2 localImageSize = imageSize;
			Rect localViewport = viewportRect;
			localViewport.Pos += localViewport.Size * this.targetRect.Pos;
			localViewport.Size *= this.targetRect.Size;
			localImageSize *= this.targetRect.Size;

			// Render the scene that contains this camera from its current point of view
			// using the previously configured drawing device.
			RenderSetup setup = this.ActiveRenderSetup;
			setup.RenderPointOfView(
				// Parent scene might be null for editor-only cameras
				this.GameObj.ParentScene ?? Scene.Current, 
				this.drawDevice, 
				localViewport, 
				localImageSize);

			// Set up drawdevice matrices so world-screen space conversions work properly
			this.drawDevice.TargetSize = imageSize;
			this.drawDevice.UpdateMatrices();
		}
		/// <summary>
		/// Renders a picking map of the current <see cref="Duality.Resources.Scene"/>.
		/// This method needs to be called each frame a picking operation is to be performed.
		/// </summary>
		/// <param name="viewportSize">Size of the viewport area to which will be rendered.</param>
		/// <param name="imageSize">Target size of the rendered image before adjusting it to fit the specified viewport.</param>
		/// <param name="renderOverlay">Whether or not to render screen overlay renderers onto the picking target.</param>
		public void RenderPickingMap(Point2 viewportSize, Vector2 imageSize, bool renderOverlay)
		{
			this.UpdateDeviceConfig();

			if (this.pickingSetup == null) this.pickingSetup = new PickingRenderSetup();
			this.pickingSetup.RenderOverlay = renderOverlay;
			this.pickingSetup.RenderPointOfView(
				// Parent scene might be null for editor-only cameras
				this.GameObj.ParentScene ?? Scene.Current, 
				this.drawDevice, 
				new Rect(viewportSize), 
				imageSize);
		}
		/// <summary>
		/// Picks the <see cref="Duality.ICmpRenderer"/> that owns the pixel at the specified position.
		/// The resulting information is only accurate if <see cref="RenderPickingMap"/> has been called this frame.
		/// </summary>
		/// <param name="x">x-Coordinate of the pixel to check.</param>
		/// <param name="y">y-Coordinate of the pixel to check.</param>
		/// <returns>The <see cref="Duality.ICmpRenderer"/> that owns the pixel.</returns>
		public ICmpRenderer PickRendererAt(int x, int y)
		{
			if (this.pickingSetup == null) return null;
			return this.pickingSetup.LookupPickingMap(x, y);
		}
		/// <summary>
		/// Picks all <see cref="Duality.ICmpRenderer">ICmpRenderers</see> contained within the specified
		/// rectangular area.
		/// The resulting information is only accurate if <see cref="RenderPickingMap"/> has been called this frame.
		/// </summary>
		/// <param name="x">x-Coordinate of the Rect.</param>
		/// <param name="y">y-Coordinate of the Rect.</param>
		/// <param name="w">Width of the Rect.</param>
		/// <param name="h">Height of the Rect.</param>
		/// <returns>A set of all <see cref="Duality.ICmpRenderer">ICmpRenderers</see> that have been picked.</returns>
		public IEnumerable<ICmpRenderer> PickRenderersIn(int x, int y, int w, int h)
		{
			if (this.pickingSetup == null) return Enumerable.Empty<ICmpRenderer>();
			return this.pickingSetup.LookupPickingMap(x, y, w, h);
		}

		/// <summary>
		/// Returns the scale factor of objects that are located at the specified (world space) z-Coordinate.
		/// </summary>
		/// <param name="z"></param>
		/// <returns></returns>
		public float GetScaleAtZ(float z)
		{
			this.UpdateDeviceConfig();
			return this.drawDevice.GetScaleAtZ(z);
		}
		/// <summary>
		/// Transforms screen space coordinates to world space coordinates. The screen positions Z coordinate is
		/// interpreted as the target world Z coordinate.
		/// </summary>
		/// <param name="screenPos"></param>
		/// <returns></returns>
		public Vector3 GetSpaceCoord(Vector3 screenPos)
		{
			this.UpdateDeviceConfig();
			return this.drawDevice.GetSpaceCoord(screenPos);
		}
		/// <summary>
		/// Transforms screen space coordinates to world space coordinates.
		/// </summary>
		/// <param name="screenPos"></param>
		/// <returns></returns>
		public Vector3 GetSpaceCoord(Vector2 screenPos)
		{
			this.UpdateDeviceConfig();
			return this.drawDevice.GetSpaceCoord(screenPos);
		}
		/// <summary>
		/// Transforms world space coordinates to screen space coordinates.
		/// </summary>
		/// <param name="spacePos"></param>
		/// <returns></returns>
		public Vector3 GetScreenCoord(Vector3 spacePos)
		{
			this.UpdateDeviceConfig();
			return this.drawDevice.GetScreenCoord(spacePos);
		}
		/// <summary>
		/// Transforms world space coordinates to screen space coordinates.
		/// </summary>
		/// <param name="spacePos"></param>
		/// <returns></returns>
		public Vector3 GetScreenCoord(Vector2 spacePos)
		{
			this.UpdateDeviceConfig();
			return this.drawDevice.GetScreenCoord(spacePos);
		}
		/// <summary>
		/// Returns whether the specified world-space position is visible in the Cameras view space.
		/// </summary>
		/// <param name="c">The position to test.</param>
		/// <param name="boundRad">The visual bounding radius to assume for the specified position.</param>
		/// <returns>True, if the position or a portion of its bounding circle is visible, false if not.</returns>
		public bool IsCoordInView(Vector3 c, float boundRad = 1.0f)
		{
			this.UpdateDeviceConfig();
			return this.drawDevice.IsCoordInView(c, boundRad);
		}

		private void SetupDevice()
		{
			if (this.drawDevice != null && !this.drawDevice.Disposed) return;
			
			this.drawDevice = new DrawDevice();

			// Default to world space render mode, so coordinate conversions
			// work as expected regardless of rendering state.
			this.drawDevice.RenderMode = RenderMatrix.WorldSpace;
		}
		private void ReleaseDevice()
		{
			if (this.drawDevice == null) return;
			this.drawDevice.Dispose();
			this.drawDevice = null;
		}
		private void UpdateDeviceConfig()
		{
			// Lazy setup, in case someone uses this Camera despite being inactive. (Editor)
			if (this.drawDevice == null) this.SetupDevice();

			this.drawDevice.RefCoord = this.gameobj.Transform.Pos;
			this.drawDevice.RefAngle = this.gameobj.Transform.Angle;
			this.drawDevice.NearZ = this.nearZ;
			this.drawDevice.FarZ = this.farZ;
			this.drawDevice.FocusDist = this.focusDist;
			this.drawDevice.Perspective = this.perspective;
			this.drawDevice.VisibilityMask = this.visibilityMask;
			this.drawDevice.ClearColor = this.clearColor;
			this.drawDevice.Target = this.renderTarget;

			this.shaderParameters.CopyTo(this.drawDevice.ShaderParameters);
		}

		void ICmpInitializable.OnInit(Component.InitContext context)
		{
			if (context == InitContext.Activate)
			{
				this.SetupDevice();
			}
		}
		void ICmpInitializable.OnShutdown(Component.ShutdownContext context)
		{
			if (context == ShutdownContext.Deactivate)
			{
				this.ReleaseDevice();
			}
		}
	}
}