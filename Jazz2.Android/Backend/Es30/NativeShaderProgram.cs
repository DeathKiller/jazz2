﻿using System;
using System.Linq;
using Duality.Resources;

using OpenTK.Graphics.ES30;

namespace Duality.Backend.Android.OpenTK
{
    public class NativeShaderProgram : INativeShaderProgram
    {
        private static NativeShaderProgram curBound;
        public static void Bind(NativeShaderProgram prog)
        {
            if (curBound == prog) return;

            if (prog == null) {
                GL.UseProgram(0);
                curBound = null;
            } else {
                GL.UseProgram(prog.Handle);
                curBound = prog;
            }
        }
        public static void SetUniform(ref ShaderFieldInfo field, int location, params float[] data)
        {
            if (field.Scope != ShaderFieldScope.Uniform) return;
            if (location == -1) return;
            switch (field.Type) {
                case ShaderFieldType.Bool:
                case ShaderFieldType.Int:
                    int[] arrI = new int[field.ArrayLength];
                    for (int j = 0; j < arrI.Length; j++) arrI[j] = (int)data[j];
                    GL.Uniform1(location, arrI.Length, arrI);
                    break;
                case ShaderFieldType.Float:
                    GL.Uniform1(location, data.Length, data);
                    break;
                case ShaderFieldType.Vec2:
                    GL.Uniform2(location, data.Length / 2, data);
                    break;
                case ShaderFieldType.Vec3:
                    GL.Uniform3(location, data.Length / 3, data);
                    break;
                case ShaderFieldType.Vec4:
                    GL.Uniform4(location, data.Length / 4, data);
                    break;
                case ShaderFieldType.Mat2:
                    GL.UniformMatrix2(location, data.Length / 4, false, data);
                    break;
                case ShaderFieldType.Mat3:
                    GL.UniformMatrix3(location, data.Length / 9, false, data);
                    break;
                case ShaderFieldType.Mat4:
                    GL.UniformMatrix4(location, data.Length / 16, false, data);
                    break;
            }
        }

        private int handle;
        private ShaderFieldInfo[] fields;
        private int[] fieldLocations;

        public int Handle
        {
            get { return this.handle; }
        }
        public ShaderFieldInfo[] Fields
        {
            get { return this.fields; }
        }
        public int[] FieldLocations
        {
            get { return this.fieldLocations; }
        }

        void INativeShaderProgram.LoadProgram(INativeShaderPart vertex, INativeShaderPart fragment)
        {
            // Removed thread guards because of performance
            //DefaultOpenTKBackendPlugin.GuardSingleThreadState();

            if (this.handle == 0)
                this.handle = GL.CreateProgram();
            else
                this.DetachShaders();

            if (vertex == null) {
                vertex = VertexShader.Minimal.Res.Native;
            }
            if (fragment == null) {
                fragment = FragmentShader.Minimal.Res.Native;
            }


            // Attach both shaders
            GL.AttachShader(this.handle, (vertex as NativeShaderPart).Handle);
            GL.AttachShader(this.handle, (fragment as NativeShaderPart).Handle);

            // Link the shader program
            GL.LinkProgram(this.handle);

            int result;
            GL.GetProgram(this.handle, ProgramParameter.LinkStatus, out result);
            if (result == 0) {
                string errorLog = GL.GetProgramInfoLog(this.handle);
                this.RollbackAtFault();
                throw new BackendException(string.Format("Linker error:{1}{0}", errorLog, Environment.NewLine));
            }

            // Collect variable infos from sub programs
            {
                NativeShaderPart vert = vertex as NativeShaderPart;
                NativeShaderPart frag = fragment as NativeShaderPart;

                ShaderFieldInfo[] fragVarArray = frag != null ? frag.Fields : null;
                ShaderFieldInfo[] vertVarArray = vert != null ? vert.Fields : null;

                if (fragVarArray != null && vertVarArray != null)
                    this.fields = vertVarArray.Union(fragVarArray).ToArray();
                else if (vertVarArray != null)
                    this.fields = vertVarArray.ToArray();
                else
                    this.fields = fragVarArray.ToArray();

            }

            // Determine each variables location
            this.fieldLocations = new int[this.fields.Length];
            for (int i = 0; i < this.fields.Length; i++) {
                if (this.fields[i].Scope == ShaderFieldScope.Uniform)
                    this.fieldLocations[i] = GL.GetUniformLocation(this.handle, this.fields[i].Name);
                else
                    this.fieldLocations[i] = GL.GetAttribLocation(this.handle, this.fields[i].Name);
            }
        }
        ShaderFieldInfo[] INativeShaderProgram.GetFields()
        {
            return this.fields.Clone() as ShaderFieldInfo[];
        }
        void IDisposable.Dispose()
        {
            if (DualityApp.ExecContext == DualityApp.ExecutionContext.Terminated)
                return;

            this.DeleteProgram();
        }

        private void DeleteProgram()
        {
            if (this.handle == 0) return;

            this.DetachShaders();
            GL.DeleteProgram(this.handle);
            this.handle = 0;
        }
        private void DetachShaders()
        {
            // Determine currently attached shaders
            int[] attachedShaders = new int[10];
            int[] attachCount = new int[1];
            GL.GetAttachedShaders(this.handle, attachedShaders.Length, attachCount, attachedShaders);

            // Detach all attached shaders
            for (int i = 0; i < attachCount[0]; i++) {
                GL.DetachShader(this.handle, attachedShaders[i]);
            }
        }
        /// <summary>
        /// In case of errors loading the program, this methods rolls back the state of this
        /// shader program, so consistency can be assured.
        /// </summary>
        private void RollbackAtFault()
        {
            this.fields = new ShaderFieldInfo[0];
            this.fieldLocations = new int[0];

            this.DeleteProgram();
        }
    }
}