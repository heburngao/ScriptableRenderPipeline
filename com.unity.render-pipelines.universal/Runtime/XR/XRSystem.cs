// XRSystem is where information about XR views and passes are read from 3 exclusive sources:
// - XRDisplaySubsystem from the XR SDK
// - the 'legacy' C++ stereo rendering path and XRSettings
// - custom XR layout (only internal for now)
// XRTODO: remove this
//#define ENABLE_VR
//#define ENABLE_XR_MODULE

using System;
using System.Collections.Generic;

#if DEVELOPMENT_BUILD || UNITY_EDITOR
using System.Diagnostics;
#endif

#if ENABLE_VR && ENABLE_XR_MODULE
using UnityEngine.XR;
#endif

namespace UnityEngine.Rendering.Universal
{
    public partial class XRSystem
    {
        // Valid empty pass when a camera is not using XR
        internal readonly XRPass emptyPass = new XRPass();

        // Store active passes and avoid allocating memory every frames
        List<XRPass> framePasses = new List<XRPass>();

        // XRTODO: expose and document public API for custom layout
        internal delegate bool CustomLayout(XRLayout layout);
        private static CustomLayout customLayout = null;
        static internal void SetCustomLayout(CustomLayout cb) => customLayout = cb;

#if ENABLE_VR && ENABLE_XR_MODULE
        // XR SDK display interface
        static List<XRDisplaySubsystem> displayList = new List<XRDisplaySubsystem>();
        XRDisplaySubsystem display = null;

        // Internal resources used by XR rendering
        Material occlusionMeshMaterial = null;
        Material mirrorViewMaterial = null;
        MaterialPropertyBlock mirrorViewMaterialProperty = new MaterialPropertyBlock();
#endif

        // Set by test framework
        internal static bool automatedTestRunning = false;

        // Used by test framework and to enable debug features
        internal static bool testModeEnabled { get => Array.Exists(Environment.GetCommandLineArgs(), arg => arg == "-xr-tests"); }

#if DEVELOPMENT_BUILD || UNITY_EDITOR
        internal static bool dumpDebugInfo = false;
        internal static List<string> passDebugInfos = new List<string>(8);
        internal static string ReadPassDebugInfo(int i) => passDebugInfos[i];
#endif
        const string k_XRMirrorTag = "XR Mirror View";
        static ProfilingSampler _XRMirrorProfilingSampler = new ProfilingSampler(k_XRMirrorTag);

        internal XRSystem()
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            RefreshXrSdk();
#endif

            // XRTODO: replace by dynamic render graph
            TextureXR.maxViews = Math.Max(TextureXR.slices, GetMaxViews());
        }

        internal void RegisterXRShaders(Shader xrOcclusionMeshPS, Shader xrMirrorViewPS)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            occlusionMeshMaterial = CoreUtils.CreateEngineMaterial(xrOcclusionMeshPS);
            mirrorViewMaterial = CoreUtils.CreateEngineMaterial(xrMirrorViewPS);
#endif
        }

#if ENABLE_VR && ENABLE_XR_MODULE
        // With XR SDK: disable legacy VR system before rendering first frame
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSplashScreen)]
        internal static void XRSystemInit()
        {
            SubsystemManager.GetInstances(displayList);

            for (int i = 0; i < displayList.Count; i++)
                displayList[i].disableLegacyRenderer = true;
        }
#endif

        // Compute the maximum number of views (slices) to allocate for texture arrays
        internal int GetMaxViews()
        {
            int maxViews = 1;

#if ENABLE_VR && ENABLE_XR_MODULE
            if (display != null)
            {
                // XRTODO : replace by API from XR SDK, assume we have 2 slices until then
                maxViews = 2;
            }
            else
#endif
            {
                if (XRGraphics.stereoRenderingMode == XRGraphics.StereoRenderingMode.SinglePassInstanced)
                    maxViews = 2;
            }

            if (testModeEnabled)
                maxViews = Math.Max(maxViews, 2);

            return maxViews;
        }

        internal List<XRPass> SetupFrame(CameraData cameraData, bool singlePassAllowed, bool singlePassTestModeActive)
        {
            Camera camera = cameraData.camera;
            bool xrSdkActive = RefreshXrSdk();

#if ENABLE_VR && ENABLE_XR_MODULE
            if (display != null)
            {
                // XRTODO: Handle stereo mode selection in URP pipeline asset UI
                display.textureLayout = XR.XRDisplaySubsystem.TextureLayout.Texture2DArray;
            }
#endif

            if (framePasses.Count > 0)
            {
                Debug.LogWarning("XRSystem.ReleaseFrame() was not called!");
                ReleaseFrame();
            }

            if ((singlePassTestModeActive || automatedTestRunning) && testModeEnabled)
                SetCustomLayout(LayoutSinglePassTestMode);
            else
                SetCustomLayout(null);

            if (camera == null)
                return framePasses;

            // Read XR SDK or legacy settings
            bool xrEnabled = xrSdkActive;

            // Enable XR layout only for gameview camera
            bool xrSupported = camera.cameraType == CameraType.Game && camera.targetTexture == null;

            if (customLayout != null && customLayout(new XRLayout() { camera = camera, xrSystem = this }))
            {
                // custom layout in used
            }
            else if (xrEnabled && xrSupported)
            {
                if (XRGraphics.renderViewportScale != 1.0f)
                {
                    Debug.LogWarning("RenderViewportScale has no effect with this render pipeline. Use dynamic resolution instead.");
                }

                if (xrSdkActive)
                {
                    CreateLayoutFromXrSdk(camera, singlePassAllowed);
                }
                else
                {
                    CreateLayoutLegacyStereo(camera);
                }
            }
            else
            {
                camera.TryGetCullingParameters(false, out var cullingParams);
                var passInfo = new XRPassCreateInfo
                {
                    multipassId = 0,
                    cullingPassId = 0,
                    cullingParameters = cullingParams,
                    renderTarget = camera.targetTexture,
                    renderTargetDesc = cameraData.cameraTargetDescriptor,
                    renderTargetIsRenderTexture = camera.targetTexture != null || camera.cameraType == CameraType.SceneView || camera.cameraType == CameraType.Preview,
                    // TODO: config renderTarget desc
                    customMirrorView = null
                };

                var viewInfo = new XRViewCreateInfo
                {
                    projMatrix = camera.projectionMatrix,
                    viewMatrix = camera.worldToCameraMatrix,
                    viewport = camera.pixelRect,
                    textureArraySlice = -1
                };

                var emptyPass = XRPass.Create(passInfo);
                emptyPass.AddView(viewInfo.projMatrix, viewInfo.viewMatrix, viewInfo.viewport, viewInfo.textureArraySlice);

                AddPassToFrame(emptyPass);
            }

            CaptureDebugInfo();

            return framePasses;
        }

        internal void ReleaseFrame()
        {
            foreach (XRPass xrPass in framePasses)
            {
                if (xrPass != emptyPass)
                    XRPass.Release(xrPass);
            }

            framePasses.Clear();
        }

        bool RefreshXrSdk()
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            SubsystemManager.GetInstances(displayList);

            if (displayList.Count > 0)
            {
                if (displayList.Count > 1)
                    throw new NotImplementedException("Only 1 XR display is supported.");

                display = displayList[0];
                display.disableLegacyRenderer = true;

                // Refresh max views
                // XRTODO: replace by dynamic render graph
                TextureXR.maxViews = Math.Max(TextureXR.slices, GetMaxViews());

                return display.running;
            }
            else
            {
                display = null;
            }

#endif

            return false;
        }

        void CreateLayoutLegacyStereo(Camera camera)
        {
            if (!camera.TryGetCullingParameters(true, out var cullingParams))
            {
                Debug.LogError("Unable to get Culling Parameters from camera!");
                return;
            }

            var passCreateInfo = new XRPassCreateInfo
            {
                multipassId = 0,
                cullingPassId = 0,
                cullingParameters = cullingParams,
                renderTarget = camera.targetTexture,
                renderTargetIsRenderTexture = false,
                customMirrorView = null
            };

            if (XRGraphics.stereoRenderingMode == XRGraphics.StereoRenderingMode.MultiPass)
            {
                if (camera.stereoTargetEye == StereoTargetEyeMask.Both || camera.stereoTargetEye == StereoTargetEyeMask.Left)
                {
                    var pass = XRPass.Create(passCreateInfo);
                    pass.AddView(camera, Camera.StereoscopicEye.Left, 0);

                    AddPassToFrame(pass);
                    passCreateInfo.multipassId++;
                }


                if (camera.stereoTargetEye == StereoTargetEyeMask.Both || camera.stereoTargetEye == StereoTargetEyeMask.Right)
                {
                    var pass = XRPass.Create(passCreateInfo);
                    pass.AddView(camera, Camera.StereoscopicEye.Right, 1);

                    AddPassToFrame(pass);
                }
            }
            else
            {
                var pass = XRPass.Create(passCreateInfo);

                if (camera.stereoTargetEye == StereoTargetEyeMask.Both || camera.stereoTargetEye == StereoTargetEyeMask.Left)
                    pass.AddView(camera, Camera.StereoscopicEye.Left, 0);

                if (camera.stereoTargetEye == StereoTargetEyeMask.Both || camera.stereoTargetEye == StereoTargetEyeMask.Right)
                    pass.AddView(camera, Camera.StereoscopicEye.Right, 1);

               AddPassToFrame(pass);
            }
        }

        void CreateLayoutFromXrSdk(Camera camera, bool singlePassAllowed)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            bool CanUseSinglePass(XRDisplaySubsystem.XRRenderPass renderPass)
            {
                if (renderPass.renderTargetDesc.dimension != TextureDimension.Tex2DArray)
                    return false;

                if (renderPass.GetRenderParameterCount() != 2 || renderPass.renderTargetDesc.volumeDepth != 2)
                    return false;

                renderPass.GetRenderParameter(camera, 0, out var renderParam0);
                renderPass.GetRenderParameter(camera, 1, out var renderParam1);

                if (renderParam0.textureArraySlice != 0 || renderParam1.textureArraySlice != 1)
                    return false;

                if (renderParam0.viewport != renderParam1.viewport)
                    return false;

                return true;
            }

            for (int renderPassIndex = 0; renderPassIndex < display.GetRenderPassCount(); ++renderPassIndex)
            {
                display.GetRenderPass(renderPassIndex, out var renderPass);
                display.GetCullingParameters(camera, renderPass.cullingPassIndex, out var cullingParams);

                if (singlePassAllowed && CanUseSinglePass(renderPass))
                {
                    var xrPass = XRPass.Create(renderPass, multipassId: framePasses.Count, cullingParams, occlusionMeshMaterial);

                    for (int renderParamIndex = 0; renderParamIndex < renderPass.GetRenderParameterCount(); ++renderParamIndex)
                    {
                        renderPass.GetRenderParameter(camera, renderParamIndex, out var renderParam);
                        xrPass.AddView(renderPass, renderParam);
                    }

                    AddPassToFrame(xrPass);
                }
                else
                {
                    for (int renderParamIndex = 0; renderParamIndex < renderPass.GetRenderParameterCount(); ++renderParamIndex)
                    {
                        renderPass.GetRenderParameter(camera, renderParamIndex, out var renderParam);

                        var xrPass = XRPass.Create(renderPass, multipassId: framePasses.Count, cullingParams, occlusionMeshMaterial);
                        xrPass.AddView(renderPass, renderParam);

                        AddPassToFrame(xrPass);
                    }
                }
            }
#endif
        }

        internal void Cleanup()
        {
            customLayout = null;

#if ENABLE_VR && ENABLE_XR_MODULE
            CoreUtils.Destroy(occlusionMeshMaterial);
            CoreUtils.Destroy(mirrorViewMaterial);
#endif
        }

        internal void AddPassToFrame(XRPass xrPass)
        {
            framePasses.Add(xrPass);
        }

        internal static class XRShaderIDs
        {
            public static readonly int _BlitTexture       = Shader.PropertyToID("_BlitTexture");
            public static readonly int _BlitScaleBias     = Shader.PropertyToID("_BlitScaleBias");
            public static readonly int _BlitScaleBiasRt   = Shader.PropertyToID("_BlitScaleBiasRt");
            public static readonly int _BlitTexArraySlice = Shader.PropertyToID("_BlitTexArraySlice");

        }

        internal void RenderMirrorView(CommandBuffer cmd)
        {
#if ENABLE_VR && ENABLE_XR_MODULE
            if (display == null || !display.running)
                return;

            if (!mirrorViewMaterial)
                return;

            using (new ProfilingScope(cmd, _XRMirrorProfilingSampler))
            {
                cmd.SetRenderTarget(BuiltinRenderTextureType.CameraTarget);

                int mirrorBlitMode = display.GetPreferredMirrorBlitMode();
                if (display.GetMirrorViewBlitDesc(null, out var blitDesc, mirrorBlitMode))
                {
                    if (blitDesc.nativeBlitAvailable)
                    {
                        display.AddGraphicsThreadMirrorViewBlit(cmd, blitDesc.nativeBlitInvalidStates, mirrorBlitMode);
                    }
                    else
                    {
                        for (int i = 0; i < blitDesc.blitParamsCount; ++i)
                        {
                            blitDesc.GetBlitParameter(i, out var blitParam);

                            Vector4 scaleBias = new Vector4(blitParam.srcRect.width, blitParam.srcRect.height, blitParam.srcRect.x, blitParam.srcRect.y);
                            Vector4 scaleBiasRT = new Vector4(blitParam.destRect.width, blitParam.destRect.height, blitParam.destRect.x, blitParam.destRect.y);

                            mirrorViewMaterialProperty.SetTexture(XRShaderIDs._BlitTexture, blitParam.srcTex);
                            mirrorViewMaterialProperty.SetVector(XRShaderIDs._BlitScaleBias, scaleBias);
                            mirrorViewMaterialProperty.SetVector(XRShaderIDs._BlitScaleBiasRt, scaleBiasRT);
                            mirrorViewMaterialProperty.SetInt(XRShaderIDs._BlitTexArraySlice, blitParam.srcTexArraySlice);

                            int shaderPass = (blitParam.srcTex.dimension == TextureDimension.Tex2DArray) ? 1 : 0;
                            cmd.DrawProcedural(Matrix4x4.identity, mirrorViewMaterial, shaderPass, MeshTopology.Quads, 4, 1, mirrorViewMaterialProperty);
                        }
                    }
                }
                else
                {
                    cmd.ClearRenderTarget(true, true, Color.black);
                }
            }
#endif
        }

        void CaptureDebugInfo()
        {
#if DEVELOPMENT_BUILD || UNITY_EDITOR
            if (dumpDebugInfo)
            {
                passDebugInfos.Clear();

                for (int passIndex = 0; passIndex < framePasses.Count; passIndex++)
                {
                    var pass = framePasses[passIndex];
                    for (int viewIndex = 0; viewIndex < pass.viewCount; viewIndex++)
                    {
                        var viewport = pass.GetViewport(viewIndex);
                        passDebugInfos.Add(string.Format("    Pass {0} Cull {1} View {2} Slice {3} : {4} x {5}",
                            pass.multipassId,
                            pass.cullingPassId,
                            viewIndex,
                            pass.GetTextureArraySlice(viewIndex),
                            viewport.width,
                            viewport.height));
                    }
                }
            }

            while (passDebugInfos.Count < passDebugInfos.Capacity)
                passDebugInfos.Add("inactive");
#endif
        }

        bool LayoutSinglePassTestMode(XRLayout frameLayout)
        {
            Camera camera = frameLayout.camera;

            if (camera != null && camera.cameraType == CameraType.Game && camera.TryGetCullingParameters(false, out var cullingParams))
            {
                cullingParams.stereoProjectionMatrix = camera.projectionMatrix;
                cullingParams.stereoViewMatrix = camera.worldToCameraMatrix;

                var passInfo = new XRPassCreateInfo
                {
                    multipassId = 0,
                    cullingPassId = 0,
                    cullingParameters = cullingParams,
                    renderTarget = camera.targetTexture,
                    customMirrorView = null
                };

                var viewInfo = new XRViewCreateInfo
                {
                    projMatrix = camera.projectionMatrix,
                    viewMatrix = camera.worldToCameraMatrix,
                    viewport = camera.pixelRect,
                    textureArraySlice = -1
                };

                // single-pass 2x rendering
                {
                    XRPass pass = frameLayout.CreatePass(passInfo);

                    for (int viewIndex = 0; viewIndex < TextureXR.slices; viewIndex++)
                        frameLayout.AddViewToPass(viewInfo, pass);
                }

                // valid layout
                return true;
            }

            return false;
        }
    }
}
