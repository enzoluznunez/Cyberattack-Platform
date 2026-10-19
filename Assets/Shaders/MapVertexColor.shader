Shader "Custom/MapVertexColor"
{
    // The bars' unlit vertex colour, with true circles, and lines that keep
    // their width however large the map is drawn.
    //
    // _Grow undoes the map's scale and applies the tool panel's: the panel's
    // scale over the map's, 3 with the panel at 0.75 and the map drawn at a
    // quarter of its box. A line's vertex stands on the line's middle and
    // moves out along its side vector (TEXCOORD2) times _Grow, so a line is
    // as wide on the headset as the panel's own at any scale, never thinning
    // below a pixel or thickening into a band. The depth between the map's layers is held the same way, so
    // they never come close enough to fight however small the map is.
    //
    // A circle is cut per pixel, not along a polygon, and smoothed across its
    // rim by alpha to coverage: TEXCOORD0 is the vertex's offset from the
    // circle's centre before it grows, and TEXCOORD1 its radius, a part that
    // scales with the map (a pin's, which counts breaches) and a part that
    // grows with _Grow (a line's round end, a selection ring's width). A
    // radius of zero is not a circle and is drawn whole.
    Properties
    {
        [Enum(UnityEngine.Rendering.CullMode)] _Cull ("Cull", Float) = 0
        _Grow ("Panel scale over map scale", Float) = 1
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Opaque"
            "RenderPipeline" = "UniversalPipeline"
            "Queue" = "AlphaTest"
            "IgnoreProjector" = "True"
        }

        Pass
        {
            Name "Unlit"

            ZWrite On
            Cull [_Cull]
            AlphaToMask On

            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            CBUFFER_START(UnityPerMaterial)
                float _Cull;
                float _Grow;
            CBUFFER_END

            struct Attributes
            {
                float4 positionOS : POSITION;
                half4 color : COLOR;
                float2 offset : TEXCOORD0;
                float2 radius : TEXCOORD1;
                float2 grow : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct Varyings
            {
                float4 positionCS : SV_POSITION;
                half4 color : COLOR;
                float2 offset : TEXCOORD0;
                float radius : TEXCOORD1;
                UNITY_VERTEX_OUTPUT_STEREO
            };

            Varyings vert(Attributes input)
            {
                Varyings output;
                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_INITIALIZE_VERTEX_OUTPUT_STEREO(output);
                float3 p = input.positionOS.xyz;
                p.xy += input.grow * _Grow;
                p.z *= _Grow;
                output.positionCS = TransformObjectToHClip(p);
                output.color = input.color;
                output.offset = input.offset + input.grow * _Grow;
                output.radius = input.radius.x + input.radius.y * _Grow;
                return output;
            }

            half4 frag(Varyings input) : SV_Target
            {
                UNITY_SETUP_STEREO_EYE_INDEX_POST_VERTEX(input);
                half4 color = input.color;
                // Worked out for every pixel, circle or not, so the
                // derivative is taken where every pixel around it takes it.
                float circle = step(1e-9, input.radius);
                float d = length(input.offset) / max(input.radius, 1e-9) * circle;
                // About a pixel's worth of the distance, so the rim fades
                // over one pixel whatever size the circle is drawn at.
                float pixel = max(fwidth(d), 1e-5);
                clip(1.0 + pixel - d);
                color.a = lerp(1.0, saturate((1.0 - d) / pixel + 0.5), circle);
                return color;
            }
            ENDHLSL
        }
    }
}
