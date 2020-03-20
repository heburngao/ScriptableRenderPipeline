#ifndef UNIVERSAL_PARTICLES_UNLIT_FORWARD_PASS_INCLUDED
#define UNIVERSAL_PARTICLES_UNLIT_FORWARD_PASS_INCLUDED

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

    float4 positionWS               : TEXCOORD1; // xyz: positionWS, w: fogCoord

    float3 normalWS                 : TEXCOORD2;
#ifdef _NORMALMAP
    float4 tangentWS                : TEXCOORD3;    // xyz: tangent, w: sign
#endif

#if defined(_FLIPBOOKBLENDING_ON)
    float3 texcoord2AndBlend        : TEXCOORD4;
#endif
#if defined(_SOFTPARTICLES_ON) || defined(_FADING_ON) || defined(_DISTORTION_ON)
    float4 projectedPosition        : TEXCOORD5;
#endif

    float4 clipPos                  : SV_POSITION;
    UNITY_VERTEX_INPUT_INSTANCE_ID
    UNITY_VERTEX_OUTPUT_STEREO
};

void InitializeInputData(VaryingsParticle input, half3 normalTS, out InputData output)
{
    output = (InputData)0;

    output.positionWS = input.positionWS.xyz;

#ifdef _NORMALMAP 
    float sgn = input.tangentWS.w;      // should be either +1 or -1
    float3 bitangent = sgn * cross(input.normalWS.xyz, input.tangentWS.xyz);
    output.normalWS = TransformTangentToWorld(normalTS, half3x3(input.tangentWS.xyz, bitangent.xyz, input.normalWS.xyz));
#else
    output.normalWS = NormalizeNormalPerPixel(input.normalWS);
#endif

    output.normalWS = NormalizeNormalPerPixel(output.normalWS);
    output.fogCoord = (half)input.positionWS.w;
}

///////////////////////////////////////////////////////////////////////////////
//                  Vertex and Fragment functions                            //
///////////////////////////////////////////////////////////////////////////////

VaryingsParticle vertParticleUnlit(AttributesParticle input)
{
    VaryingsParticle output = (VaryingsParticle)0;

    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_TRANSFER_INSTANCE_ID(input, output);
    UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);

    VertexPositionInputs vertexInput = GetVertexPositionInputs(input.vertex.xyz);
    VertexNormalInputs normalInput = GetVertexNormalInputs(input.normal, input.tangentOS);

    // position ws is used to compute eye depth in vertFading
    output.positionWS.xyz = vertexInput.positionWS;
    output.positionWS.w = ComputeFogFactor(vertexInput.positionCS.z);
    output.clipPos = vertexInput.positionCS;
    output.color = input.color;

    // already normalized from normal transform to WS.
    output.normalWS = normalInput.normalWS;
#ifdef _NORMALMAP
    real sign = input.tangentOS.w * GetOddNegativeScale();
    output.tangentWS = half4(normalInput.tangentWS.xyz, sign);
#endif

    output.texcoord = input.texcoords.xy;
#ifdef _FLIPBOOKBLENDING_ON
    output.texcoord2AndBlend.xy = input.texcoords.zw;
    output.texcoord2AndBlend.z = input.texcoordBlend;
#endif

#if defined(_SOFTPARTICLES_ON) || defined(_FADING_ON) || defined(_DISTORTION_ON)
    output.projectedPosition = ComputeScreenPos(vertexInput.positionCS);
#endif

    return output;
}

half4 fragParticleUnlit(VaryingsParticle input) : SV_Target
{
    UNITY_SETUP_INSTANCE_ID(input);
    UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);

    float2 uv = input.texcoord;
    float3 blendUv = float3(0, 0, 0);
#if defined(_FLIPBOOKBLENDING_ON)
    blendUv = input.texcoord2AndBlend;
#endif

    float4 projectedPosition = float4(0,0,0,0);
#if defined(_SOFTPARTICLES_ON) || defined(_FADING_ON) || defined(_DISTORTION_ON)
    projectedPosition = input.projectedPosition;
#endif

    half4 albedo = SampleAlbedo(uv, blendUv, _BaseColor, input.color, projectedPosition, TEXTURE2D_ARGS(_BaseMap, sampler_BaseMap));
    half3 normalTS = SampleNormalTS(uv, blendUv, TEXTURE2D_ARGS(_BumpMap, sampler_BumpMap));

#if defined (_DISTORTION_ON)
    albedo.rgb = Distortion(albedo, normalTS, _DistortionStrengthScaled, _DistortionBlend, projectedPosition);
#endif

#if defined(_EMISSION)
    half3 emission = BlendTexture(TEXTURE2D_ARGS(_EmissionMap, sampler_EmissionMap), uv, blendUv).rgb * _EmissionColor.rgb;
#else
    half3 emission = half3(0, 0, 0);
#endif

    half3 result = albedo.rgb + emission;
    half fogFactor = input.positionWS.w;
    result = MixFogColor(result, half3(0, 0, 0), fogFactor);
    albedo.a = OutputAlpha(albedo.a);

    return half4(result, albedo.a);
}

#endif // UNIVERSAL_PARTICLES_UNLIT_FORWARD_PASS_INCLUDED
