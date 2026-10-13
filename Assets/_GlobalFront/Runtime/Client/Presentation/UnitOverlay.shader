Shader "GlobalFront/Unit Overlay"
{
    // Both overlay kinds read one instanced float4 per unit, and the two passes
    // differ in what that float4 carries, how it is shaded, and how it treats depth:
    // a colour for the ground ring, (fraction, colour) for the billboarded health bar.
    // Two passes in one shader rather than two shaders because the overlay is one
    // material asset and the instance matrices already put each kind in its own plane.
    //
    // Cull Off and ZWrite Off are load-bearing, not taste: the ring and bar quads are
    // generated in code (UnitOverlayGeometry), the bar flips to face the camera so its
    // winding changes with the view, and neither overlay should ever hide anything.
    // The depth test is load-bearing too, and it is the one thing the two kinds cannot
    // share — see the passes.
    Properties
    {
        _RingThickness("Ring thickness", Range(0.02, 0.5)) = 0.12
        _BarBorder("Bar border", Range(0.0, 0.4)) = 0.12
    }

    SubShader
    {
        Tags
        {
            "RenderType" = "Transparent"
            "Queue" = "Transparent"
            "IgnoreProjector" = "True"
            "PreviewType" = "Plane"
            "RenderPipeline" = "UniversalPipeline"
        }

        Cull Off
        ZWrite Off
        // Depth-tested for the ring, which lies on the ground and must go under the
        // terrain it is drawn on. Overridden to Always by the health bar pass below:
        // a bar is interface, and an interface that a unit's own hull can hide is not
        // readable.
        ZTest LEqual
        Blend SrcAlpha OneMinusSrcAlpha

        Pass
        {
            Name "UnitOverlayRing"
            Tags { "LightMode" = "UnitOverlayRing" }

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex RingVertex
            #pragma fragment RingFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _RingThickness;

            UNITY_INSTANCING_BUFFER_START(Overlay)
                UNITY_DEFINE_INSTANCED_PROP(float4, _OverlayColor)
            UNITY_INSTANCING_BUFFER_END(Overlay)

            struct RingAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct RingVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float4 color : TEXCOORD1;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            RingVaryings RingVertex(RingAttributes input)
            {
                RingVaryings output = (RingVaryings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                // TransformObjectToHClip rather than a hand-rolled mul(UNITY_MATRIX_VP,
                // mul(UNITY_MATRIX_M, ...)): under camera-relative rendering the
                // instance matrix is pre-translated, and only these helpers apply the
                // correction consistently.
                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;
                output.color = UNITY_ACCESS_INSTANCED_PROP(Overlay, _OverlayColor);
                return output;
            }

            half4 RingFragment(RingVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                // The quad spans the ring's bounding box, so uv centre-distance is the
                // radius in units of the ring itself and the thickness is resolution
                // independent. fwidth keeps the edge one pixel wide at any distance;
                // without it a ring shrinks into a flickering dot at map zoom.
                float2 offset = input.uv * 2.0 - 1.0;
                float radius = length(offset);
                float inner = 1.0 - _RingThickness;
                float aa = max(fwidth(radius), 1e-5);
                float band = smoothstep(inner - aa, inner + aa, radius)
                    * (1.0 - smoothstep(1.0 - aa, 1.0 + aa, radius));

                half4 color = input.color;
                color.a *= band;
                if (color.a < 0.01)
                {
                    discard;
                }

                return color;
            }
            ENDHLSL
        }

        Pass
        {
            Name "UnitOverlayHealthBar"
            Tags { "LightMode" = "UnitOverlayHealthBar" }

            // The SubShader's LEqual is wrong for a bar and cannot be fixed by raising
            // the anchor: the prototype hulls this slice builds are 2.2 m (scout) and
            // 2.6 m (tank) above a ground-pinned origin, with turrets to 2.95 m, so any
            // single height constant that clears one archetype is inside another. A bar
            // that is drawn over the unit it reports on is the behaviour every RTS of
            // this genre ships, and ZWrite Off above keeps it from hiding anything else.
            ZTest Always

            HLSLPROGRAM
            #pragma target 3.0
            #pragma vertex BarVertex
            #pragma fragment BarFragment
            #pragma multi_compile_instancing

            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            float _BarBorder;

            UNITY_INSTANCING_BUFFER_START(Overlay)
                // x: health fraction (already clamped on the CPU), yzw: fill colour.
                UNITY_DEFINE_INSTANCED_PROP(float4, _HealthBarParams)
            UNITY_INSTANCING_BUFFER_END(Overlay)

            struct BarAttributes
            {
                float4 positionOS : POSITION;
                float2 uv : TEXCOORD0;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            struct BarVaryings
            {
                float4 positionCS : SV_POSITION;
                float2 uv : TEXCOORD0;
                float3 color : TEXCOORD1;
                float fraction : TEXCOORD2;
                UNITY_VERTEX_INPUT_INSTANCE_ID
            };

            BarVaryings BarVertex(BarAttributes input)
            {
                BarVaryings output = (BarVaryings)0;

                UNITY_SETUP_INSTANCE_ID(input);
                UNITY_TRANSFER_INSTANCE_ID(input, output);

                output.positionCS = TransformObjectToHClip(input.positionOS.xyz);
                output.uv = input.uv;

                float4 parameters = UNITY_ACCESS_INSTANCED_PROP(Overlay, _HealthBarParams);
                output.fraction = parameters.x;
                output.color = parameters.yzw;
                return output;
            }

            half4 BarFragment(BarVaryings input) : SV_Target
            {
                UNITY_SETUP_INSTANCE_ID(input);

                float2 uv = input.uv;

                // Distance to the nearest edge, in uv: under one border unit is the
                // dark frame that makes the bar readable against bright terrain.
                float edge = min(min(uv.x, 1.0 - uv.x), min(uv.y, 1.0 - uv.y));
                if (edge >= _BarBorder)
                {
                    float span = 1.0 - 2.0 * _BarBorder;
                    float filled = (uv.x - _BarBorder) / max(span, 1e-5);
                    return filled <= input.fraction
                        ? half4(input.color, 1.0)
                        : half4(0.04, 0.04, 0.05, 0.85);
                }

                return half4(0.0, 0.0, 0.0, 0.75);
            }
            ENDHLSL
        }
    }

    Fallback Off
}
