using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.HighDefinition;
using UnityEngine.Rendering.HighDefinition.Attributes;
using UnityEngine.Video;

using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.HighDefinition.Compositor
{
    // The main entry point for the compositing operations. Manages the list of layers, output displays, etc.
    [ExecuteAlways]
    internal class CompositionManager : MonoBehaviour
    {
        // These shader graph properties should be properly set when drawing a full screen quad for the compositor output
        internal class SGShaderIDs
        {
            public static readonly int _ViewProjMatrix = Shader.PropertyToID("_ViewProjMatrix");
            public static readonly int _WorldSpaceCameraPos = Shader.PropertyToID("_WorldSpaceCameraPos");
        }

        public enum OutputDisplay
        {
            Display1 = 0,
            Display2,
            Display3,
            Display4,
            Display5,
            Display6,
            Display7,
            Display8
        }

        public enum AlphaChannelSupport
        {
            None = 0,
            Rendering,
            RenderingAndPostProcessing
        }

        [SerializeField] Material m_Material;

        [SerializeField] OutputDisplay m_OutputDisplay = OutputDisplay.Display1;

        [SerializeField] List<CompositorLayer> m_InputLayers = new List<CompositorLayer>();

        public AlphaChannelSupport alphaSupport
        {
            get => m_AlphaSupport;
        }
        internal AlphaChannelSupport m_AlphaSupport;

        public bool enableOutput
        {
            get
            {
                if (m_OutputCamera)
                {
                    return m_OutputCamera.enabled;
                }
                return false;
            }
            set
            {
                if (m_OutputCamera)
                {
                    m_OutputCamera.enabled = value;

                    // also change the layers
                    foreach(var layer in m_InputLayers)
                    {
                        if (layer.camera)
                        {
                            layer.camera.enabled = value;
                        }
                    }

                }
            }
        }

        public int numLayers
        {
            get
            {
                if (m_CompositionProfile)
                {
                    return m_CompositionProfile.m_InputLayers.Count;
                }
                return 0;
            }
        }

        public Shader shader
        {
            get => m_Shader;
            set
            {
                m_Shader = value;
            }
        }

        [SerializeField] Shader m_Shader;

        public CompositionProfile profile
        {
            get => m_CompositionProfile;
            set
            {
                m_CompositionProfile = value;
                m_InputLayers = m_CompositionProfile.m_InputLayers;
            }
        }

        [HideInInspector, SerializeField] CompositionProfile m_CompositionProfile;
        public Camera outputCamera
        {
            get => m_OutputCamera;
            set
            {
                m_OutputCamera = value;
            }
        }

        [SerializeField] Camera m_OutputCamera;

        public float aspectRatio
        {
            get
            {
                if (m_CompositionProfile)
                {
                    return m_CompositionProfile.aspectRatio;
                }
                return 1.0f;
            }
        }

        public bool shaderPropertiesAreDirty
        {
            set
            {
                m_ShaderPropertiesAreDirty = true;
            }
        }

        internal bool m_ShaderPropertiesAreDirty = false;

        internal Matrix4x4 m_ViewProjMatrix;
        internal Matrix4x4 m_ViewProjMatrixFlipped;
        internal GameObject m_CompositorGameObject;

        static private CompositionManager s_CompositorInstance;

        #region Validation
        // Validates the rendering pipeline and fixes potential issues
        bool ValidatePipeline()
        {
            var hdPipeline = RenderPipelineManager.currentPipeline as HDRenderPipeline;
            if (hdPipeline != null)
            {
                m_AlphaSupport = AlphaChannelSupport.RenderingAndPostProcessing;
                if (hdPipeline.asset.currentPlatformRenderPipelineSettings.colorBufferFormat == RenderPipelineSettings.ColorBufferFormat.R11G11B10)
                {
                    m_AlphaSupport = AlphaChannelSupport.None;
                }
                else if (hdPipeline.asset.currentPlatformRenderPipelineSettings.postProcessSettings.bufferFormat == PostProcessBufferFormat.R11G11B10)
                {
                    m_AlphaSupport = AlphaChannelSupport.Rendering;
                }

                int indx = hdPipeline.asset.beforePostProcessCustomPostProcesses.FindIndex(x => x == typeof(ChromaKeying).AssemblyQualifiedName);
                if (indx < 0)
                {
                    Debug.Log("Registering chroma keying pass for the HDRP pipeline");
                    hdPipeline.asset.beforePostProcessCustomPostProcesses.Add(typeof(ChromaKeying).AssemblyQualifiedName);
                }

                indx = hdPipeline.asset.beforePostProcessCustomPostProcesses.FindIndex(x => x == typeof(AlphaInjection).AssemblyQualifiedName);
                if (indx < 0)
                {
                    Debug.Log("Registering alpha injection pass for the HDRP pipeline");
                    hdPipeline.asset.beforePostProcessCustomPostProcesses.Add(typeof(AlphaInjection).AssemblyQualifiedName);
                }
                return true;
            }
            return false;
        }
        bool ValidateCompositionShader()
        {
            if (m_Shader == null)
            {
                return false;
            }

            if (m_CompositionProfile == null)
            {
                Debug.Log("A composition profile was not found. Set the composition graph from the Compositor window to create one.");
                return false;
            }

            return true;
        }

        bool ValidateProfile()
        {
            if (m_CompositionProfile)
            {
                return true;
            }
            else
            {
                Debug.LogError("No composition profile was found! Use the compositor tool to create one.");
                return false;
            }
        }

        bool ValidateMainCompositorCamera()
        {
            if (m_OutputCamera == null)
            {
                return false;
            }

            var cameraData = m_OutputCamera.GetComponent<HDAdditionalCameraData>();
            if (cameraData == null)
            {
                m_OutputCamera.gameObject.AddComponent(typeof(HDAdditionalCameraData));
                cameraData = m_OutputCamera.GetComponent<HDAdditionalCameraData>();
            }

            // Setup custom rendering (we don't want HDRP to compute anything in this camera)
            if (cameraData)
            {
                cameraData.customRender += CustomRender;
            }
            else
            {
                Debug.Log("Null additional data in compositor output");
            }
            return true;
        }

        bool ValidateAndFixRuntime()
        {
            if (m_OutputCamera == null)
            {
                Debug.Log("No camera was found");
                return false;
            }

            if (m_Shader == null)
            {
                Debug.Log("The composition shader graph must be set");
                return false;
            }

            if (m_CompositionProfile == null)
            {
                Debug.Log("The composition profile was not set at runtime");
                return false;
            }

            if (m_Material == null)
            {
                Debug.Log("Null Material!");
                SetupCompositionMaterial();
            }

            var cameraData = m_OutputCamera.GetComponent<HDAdditionalCameraData>();
            if (cameraData && !cameraData.hasCustomRender)
            {
                cameraData.customRender += CustomRender;
            }

            return true;
        }
        #endregion

        // This is called when we change camera, to remove the custom draw callback from the old camera before we set the new one
        public void DropCompositorCamera()
        {
            if (m_OutputCamera)
            {
                var cameraData = m_OutputCamera.GetComponent<HDAdditionalCameraData>();
                if (cameraData && cameraData.hasCustomRender)
                {
                    cameraData.customRender -= CustomRender;
                }
            }
        }

        public void Init()
        {
            if ( ValidateCompositionShader() && ValidateProfile() && ValidateMainCompositorCamera())
            {
                UpdateDisplayNumber();

                SetupCompositionMaterial();

                SetupCompositorLayers();

                SetupGlobalCompositorVolume();

                SetupCompositorConstants();

                SetupLayerPriorities();
            }
            else
            {
                Debug.LogError("The compositor was disabled due to a validation error in the configuration.");
                enabled = false;
            }
        }

        // Start is called before the first frame update
        void Start()
        {
            Init();
        }

        public void UpdateLayerList()
        {
            if (m_CompositionProfile)
            {
                m_CompositionProfile.m_InputLayers = m_InputLayers; 
            }
        }

        void OnValidate()
        {
            UpdateLayerList();
        }

        public void OnEnable()
        {
            enableOutput = true;
            s_CompositorInstance = null;
#if UNITY_EDITOR
            //This is a work-around, to make edit and continue work when editing source code
            UnityEditor.AssemblyReloadEvents.afterAssemblyReload += OnAfterAssemblyReload;
#endif
        }

        public void DeleteLayerRTs()
        {
            // delete the layer from last to first, in order to release first the camera and then the associated RT
            for (int i = m_CompositionProfile.m_InputLayers.Count - 1; i >= 0; --i)
            {
                m_CompositionProfile.m_InputLayers[i].DestroyRT();
            }
        }

        public bool IsOutputLayer(int layerID)
        {
            if (layerID >= 0 && layerID < m_CompositionProfile.m_InputLayers.Count)
            {
                if (m_CompositionProfile.m_InputLayers[layerID].GetOutputTarget() == CompositorLayer.OutputTarget.CameraStack)
                {
                    return false;
                }
            }
            return true;
        }

        public void UpdateDisplayNumber()
        {
            if (m_OutputCamera)
            {
                m_OutputCamera.targetDisplay = (int)m_OutputDisplay;
            }
        }

        void SetupCompositorLayers()
        {
            if (m_CompositionProfile)
            {
                m_CompositionProfile.Init();
            }
        }

        public void SetNewCompositionShader()
        {
            // When we load a new shader, we need to clear the serialized material. 
            m_Material = null;
            SetupCompositionMaterial();
        }

        public void SetupCompositionMaterial()
        {
            // Create the composition material
            if (m_Shader)
            {
                if (m_Material == null)
                {
                    m_Material = new Material(m_Shader);
                }

                m_CompositionProfile.AddPropertiesFromShaderAndMaterial(m_Shader, m_Material);

                //TODO: this is mostly a hack, the compositor profile was not serialized properly, and for this reason we also keep a reference of this list outside of the profile.
                m_InputLayers = m_CompositionProfile.m_InputLayers;
            }
            else
            {
                Debug.LogError("Cannot find the default composition graph. Was the installation folder corrupted?");
                m_Material = null;
            }
        }

        public void SetupLayerPriorities()
        {
            if (m_CompositionProfile)
            {
                m_CompositionProfile.SetDrawOrder();
            }
        }

        public void OnAfterAssemblyReload()
        {
            // Bug? : After assembly reload, the customRender callback is dropped, so set it again
            var cameraData = m_OutputCamera.GetComponent<HDAdditionalCameraData>();
            if (cameraData && !cameraData.hasCustomRender)
            {
                cameraData.customRender += CustomRender;
            }
        }

        public void OnDisable()
        {
            enableOutput = false;
        }

        // Setup a global volume used for chroma keying, alpha injection etc
        void SetupGlobalCompositorVolume()
        {
            var compositorVolumes = Resources.FindObjectsOfTypeAll(typeof(CustomPassVolume));
            foreach (CustomPassVolume volume in compositorVolumes)
            {
                if(volume.isGlobal && volume.injectionPoint == CustomPassInjectionPoint.BeforeRendering)
                {
                    Debug.LogWarning($"A custom volume pass with name ${volume.name} was already registered on the BeforeRendering injection point.");
                }
            }

            // Instead of using one volume per layer/camera, we setup one global volume and we store the data in the camera
            // This way the compositor has to use only one layer/volume for N cameras (instead of N).
            m_CompositorGameObject = new GameObject("Global Composition Volume") { hideFlags = HideFlags.HideAndDontSave };
            Volume globalPPVolume = m_CompositorGameObject.AddComponent<Volume>();
            globalPPVolume.gameObject.layer = 31;
            AlphaInjection injectAlphaNode = globalPPVolume.profile.Add<AlphaInjection>();
            ChromaKeying chromaKeyingPass = globalPPVolume.profile.Add<ChromaKeying>();
            chromaKeyingPass.activate.Override(true);

            // Custom pass for "Clear to Texture"
            CustomPassVolume globalCustomPassVolume = m_CompositorGameObject.AddComponent<CustomPassVolume>();
            globalCustomPassVolume.injectionPoint = CustomPassInjectionPoint.BeforeRendering;
            globalCustomPassVolume.AddPassOfType(typeof(CustomClear));
        }

        void SetupCompositorConstants()
        {
            m_ViewProjMatrix = Matrix4x4.Scale(new Vector3(2.0f, 2.0f, 0.0f)) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.0f));
            m_ViewProjMatrixFlipped = Matrix4x4.Scale(new Vector3(2.0f, -2.0f, 0.0f)) * Matrix4x4.Translate(new Vector3(-0.5f, -0.5f, 0.0f));
        }

        public void UpdateLayerSetup()
        {
            SetupCompositorLayers();
            SetupLayerPriorities();
        }

        // Update is called once per frame
        void Update()
        {
            if (ValidatePipeline() == false || ValidateAndFixRuntime() == false || profile.RuntimeCheck() == false)
            {
                return;
            }

            UpdateDisplayNumber();

#if UNITY_EDITOR
            if (m_ShaderPropertiesAreDirty)
            {
                SetNewCompositionShader();
                m_ShaderPropertiesAreDirty = false;
                SetupCompositorLayers();//< required to allocate RT for the new layers
            }

            foreach (var layer in m_InputLayers)
            {
                if (!layer.ValidateRTSize(m_OutputCamera.pixelWidth, m_OutputCamera.pixelHeight))
                {
                    DeleteLayerRTs();
                    SetupCompositorLayers();
                    break;
                }
            }
#endif

            if (m_CompositionProfile)
            {
                m_CompositionProfile.UpdateLayers();
            }
        }

        void OnDestroy()
        {
            if (m_CompositionProfile)
            {
                m_CompositionProfile.OnDestroy();
            }

            if (m_CompositorGameObject != null)
            {
                CoreUtils.Destroy(m_CompositorGameObject);
                m_CompositorGameObject = null;
            }

            var compositorVolumes = Resources.FindObjectsOfTypeAll(typeof(CustomPassVolume));
            foreach (CustomPassVolume volume in compositorVolumes)
            {
                if (volume.name == "Global Composition Volume" && volume.injectionPoint == CustomPassInjectionPoint.BeforeRendering)
                {
                    CoreUtils.Destroy(volume);
                }
            }
        }

        public void AddInputFilterAtLayer(CompositionFilter filter, int index)
        {
            m_CompositionProfile.m_InputLayers[index].AddInputFilter(filter);
        }

        public void AddNewLayer(int index, CompositorLayer.LayerType type = CompositorLayer.LayerType.Camera)
        {
            var newLayer = CompositorLayer.CreateStackLayer(type, "New SubLayer");
            m_CompositionProfile.AddNewLayerAtIndex(newLayer, index);
        }

        public void RemoveLayerAtIndex(int indx)
        {
            m_CompositionProfile.RemoveLayerAtIndex(indx);
        }

        public RenderTexture GetRenderTarget(int indx)
        {
            if (indx >= 0 && indx < m_CompositionProfile.m_InputLayers.Count)
            {
                return m_CompositionProfile.m_InputLayers[indx].GetRenderTarget(true, true);
            }
            return null;
        }

        void CustomRender(ScriptableRenderContext context, HDCamera camera)
        {
            if (camera == null || camera.camera == null || m_Material == null)
                return;

            // set shader uniforms

            foreach (var prop in m_CompositionProfile.m_ShaderProperties)
            {
                if (prop.m_Type == ShaderPropertyType.Float)
                {
                    m_Material.SetFloat(prop.m_PropertyName, prop.m_Value.x);
                }
                else if (prop.m_Type == ShaderPropertyType.Vector)
                {
                    m_Material.SetVector(prop.m_PropertyName, prop.m_Value);
                }
                else if (prop.m_Type == ShaderPropertyType.Range)
                {
                    m_Material.SetFloat(prop.m_PropertyName, prop.m_Value.x);
                }
                else if (prop.m_Type == ShaderPropertyType.Color)
                {
                    m_Material.SetColor(prop.m_PropertyName, prop.m_Value);
                }
            }

            int layerIndex = 0;
            foreach (var layer in m_CompositionProfile.m_InputLayers)
            {
                if (layer.GetOutputTarget() != CompositorLayer.OutputTarget.CameraStack)  // stacked cameras are not exposed as compositor layers 
                {
                    m_Material.SetTexture(layer.name, layer.GetRenderTarget(), RenderTextureSubElement.Color);
                }
                layerIndex++;
            }

            // Blit command
            var cmd = CommandBufferPool.Get("Compositor Blit");
            {
                cmd.SetGlobalVector(SGShaderIDs._WorldSpaceCameraPos, new Vector3(0.0f, 0.0f, 0.0f));
                cmd.SetViewport(new Rect(0, 0, camera.camera.pixelWidth, camera.camera.pixelHeight));
                cmd.ClearRenderTarget(true, false, Color.black);
            }

            if (camera.camera.targetTexture)
            {
                cmd.SetGlobalMatrix(SGShaderIDs._ViewProjMatrix, m_ViewProjMatrixFlipped);
                cmd.Blit(null, BuiltinRenderTextureType.CurrentActive, m_Material, m_Material.FindPass("ForwardOnly"));
                cmd.Blit(BuiltinRenderTextureType.CurrentActive, camera.camera.targetTexture);
            }
            else
            {
                cmd.SetGlobalMatrix(SGShaderIDs._ViewProjMatrix, m_ViewProjMatrix);
                cmd.Blit(null, BuiltinRenderTextureType.CurrentActive, m_Material, m_Material.FindPass("ForwardOnly"));
            }
            
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        static public Camera GetSceceCamera()
        {
            if (Camera.main != null)
            {
                return Camera.main;
            }
            foreach (var camera in Camera.allCameras)
            {
                if (camera.name != "MainCompositorCamera")
                {
                    return camera;
                }
            }
            Debug.LogWarning("Camera not found");
            return null;
        }

        static public CompositionManager GetInstance()
        {

            if(s_CompositorInstance != null)
            {
                return s_CompositorInstance;
            }

            s_CompositorInstance = GameObject.FindObjectOfType(typeof(CompositionManager), true) as CompositionManager;
            return s_CompositorInstance;
        }
    }
}
