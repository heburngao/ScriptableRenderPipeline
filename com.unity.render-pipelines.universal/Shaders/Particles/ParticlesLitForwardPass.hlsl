#ifndef UNIVERSAL_PARTICLES_FORWARD_LIT_PASS_INCLUDED
#define UNIVERSAL_PARTICLES_FORWARD_LIT_PASS_INCLUDED

#include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Lighting.hlsl"

struct AttributesParticle
{
    float4 vertex : POSITION;
    float3 normal : NORMAL;
    half4 color : COLOR;
#if defined(_FLIPBOOKBLENDING_ON) && !defined(UNITY_PARTICLE_INSTANCING_ENABLED)
    float4 texcoords : TEXCOORD0;
    float texcoordBlend : TEXCOORD1;
#else
    float2 texcoords : TEXCOORD0;
#endif
    float4 tangentOS : TANGENT;
    UNITY_VERTEX_INPUT_INSTANCE_ID
};

struct VaryingsParticle
{
    half4 color                     : COLOR;
    float2 texcoord                 : TEXCOORD0;

    float4 positionWS               : TEXCOORD1;

    float3 normalWS                 : TEXCOORD2;
#ifdef _NORMALMAP
    float4 tangentWS                : TEXCOORD3;    // xyz: tangent, w: sign
#endif
    float3 viewDirWS                : TEXCOORD4;

#if defined(_FLIPBOOKBLENDING_ON)
    float3 texcoord2AndBlend        : TEXCOORD5;
#endif
#if defined(_SOFTPARTICLES_ON) || defined(_FADING_ON) || defined(_DISTORTION_ON)
    float4 projectedPosition        : TEXCOORD6;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    float4 shadowCoord              : TEXCOORD7;
#endif

    float3 vertexSH                 : TEXCOORD8; // SH
    float4 clipPos                  : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(VaryingsParticle input, half3 normalTS, out InputData output)
{
    output = (InputData)0;

    output.positionWS = input.positionWS.xyz;

    half3 viewDirWS = SafeNormalize(input.viewDirWS);
#ifdef _NORMALMAP 
    float sgn = input.tangentWS.w;      // should be either +1 or -1
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    output.normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
#else
    output.normalWS = input.normalWS;
#endif

    output.normalWS = NormalizeNormalPerPixel(output.normalWS);
    output.viewDirectionWS = viewDirWS;

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = input.shadowCoord;
#elif defined(MAIN_LIGHT_CALCULATE_SHADOWS)
    output.shadowCoord = TransformWorldToShadowCoord(output.positionWS);
#else
    output.shadowCoord = float4(0, 0, 0, 0);
#endif

    output.fogCoord = (half)input.positionWS.w;
    output.vertexLighting = half3(0.0h, 0.0h, 0.0h);
    output.bakedGI = SampleSHPixel(input.vertexSH, output.normalWS);
}

///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

VaryingsParticle ParticlesLitVertex(AttributesParticle input)
{
    VaryingsParticle output = (VaryingsParticle)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    // normalWS and tangentWS already normalize.
    // this is required to avoid skewing the direction during interpolation
    // also required for per-vertex lighting and SH evaluation
    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.vertex.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normal, input.tangentOS);
    half3 viewDirWS = GetCameraPositionWS() - vertexInput.positionWS;
    half3 vertexLight = VertexLighting(vertexInput.positionWS, normalInput.normalWS);
    half fogFactor = ComputeFogFactor(vertexInput.positionCS.z);

    // already normalized from normal transform to WS.
    output.normalWS = normalInput.normalWS;
    output.viewDirWS = viewDirWS;
#ifdef _NORMALMAP
    real sign = input.tangentOS.w * GetOddNegativeScale();
    output.tangentWS = half4(normalInput.tangentWS.xyz, sign);
#endif

    OUTPUT_SH(output.normalWS.xyz, output.vertexSH);

    output.positionWS.xyz = vertexInput.positionWS;
    output.positionWS.w = fogFactor;
    output.clipPos = vertexInput.positionCS;
    output.color = input.color;

    output.texcoord = input.texcoords.xy;
#ifdef _FLIPBOOKBLENDING_ON
    output.texcoord2AndBlend.xy = input.texcoords.zw;
    output.texcoord2AndBlend.z = input.texcoordBlend;
#endif

#if defined(_SOFTPARTICLES_ON) || defined(_FADING_ON) || defined(_DISTORTION_ON)
    output.projectedPosition = vertexInput.positionNDC;
#endif

#if defined(REQUIRES_VERTEX_SHADOW_COORD_INTERPOLATOR)
    output.shadowCoord = GetShadowCoord(vertexInput);
#endif

    return output;
}

half4 ParticlesLitFragment(VaryingsParticle input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float3 blendUv = float3(0, 0, 0);
#if defined(_FLIPBOOKBLENDING_ON)
    blendUv = input.texcoord2AndBlend;
#endif

    float4 projectedPosition = float4(0,0,0,0);
#if defined(_SOFTPARTICLES_ON) || defined(_FADING_ON) || defined(_DISTORTION_ON)
    projectedPosition = input.projectedPosition;
#endif

    SurfaceData surfaceData;
    InitializeParticleLitSurfaceData(input.texcoord, blendUv, input.color, projectedPosition, surfaceData);

    InputData inputData = (InputData)0;
    InitializeInputData(input, surfaceData.normalTS, inputData);

    half4 color = UniversalFragmentPBR(inputData, surfaceData.albedo,
        surfaceData.metallic, half3(0, 0, 0), surfaceData.smoothness, surfaceData.occlusion, surfaceData.emission, surfaceData.alpha);

    color.rgb = MixFog(color.rgb, inputData.fogCoord);
    color.a = OutputAlpha(color.a);
    
    return color;
}

#endif // UNIVERSAL_PARTICLES_FORWARD_LIT_PASS_INCLUDED
