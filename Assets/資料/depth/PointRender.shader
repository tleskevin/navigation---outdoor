Shader "Custom/PointRender"
{
    Properties {
        _PointSize ("Point Size", Float) = 0.02
        _PointColor ("Point Color", Color) = (0, 1, 0, 1)
    }
    SubShader
    {
        Tags { "RenderType"="Opaque" "Queue"="Transparent+100" }
        Pass
        {
            ZWrite Off
            ZTest Always // 強制顯示
            HLSLPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include "Packages/com.unity.render-pipelines.universal/ShaderLibrary/Core.hlsl"

            struct appdata { float4 vertex : POSITION; uint instanceID : SV_InstanceID; };
            struct v2f { float4 pos : SV_POSITION; float4 color : COLOR; };
            StructuredBuffer<float3> _PointCloudBuffer;
            float _PointSize; float4 _PointColor;

            v2f vert (appdata v) {
                v2f o;
                float3 worldPos = _PointCloudBuffer[v.instanceID];
                float size = (length(worldPos) < 0.001f) ? 0 : _PointSize;
                // 使用世界座標轉換
                o.pos = mul(UNITY_MATRIX_VP, float4(worldPos + v.vertex.xyz * size, 1.0f));
                o.color = _PointColor;
                return o;
            }
            float4 frag (v2f i) : SV_Target { return i.color; }
            ENDHLSL
        }
    }
}