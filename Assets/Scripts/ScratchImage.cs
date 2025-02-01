/*
 * Author:Misaka-Mikoto
 * Date: 2021-02-08
 * Url:https://github.com/Misaka-Mikoto-Tech/ScratchImage
 */

using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.UI;

/// <summary>
/// ¿ÉÒÔ¹Î¿ªµÄÍ¼Ïñ
/// </summary>
public class ScratchImage : MonoBehaviour
{
    public struct StatData
    {
        public float    fillPercent;  // Ìî³ä°Ù·Ö±È£¨·Ç0Öµ£©
        public float    avgVal;       // Æ½¾ùÖµ
    }

    /// <summary>
    /// Ö±·½Í¼Í°µÄÊýÁ¿£¬±ØÐëÓëshaderÖÐ¶¨ÒåµÄÒ»ÖÂ, ÇÒÐ¡ÓÚ256
    /// </summary>
    public const int HISTOGRAM_BINS = 128;
    /// <summary>
    /// ÓÃÀ´¿ØÖÆÍ¸Ã÷¶ÈµÄRTÏà±ÈImage³ß´çµÄ±ÈÀý£¬ÖµÔ½Ð¡ÐÔÄÜÔ½¸ß£¬µ«ÊÇ¾«¶ÈºÍÐ§¹ûÒ²Ô½²î
    /// </summary>
    public const float ALPHA_RT_SCALE = 0.4f;
    /// <summary>
    /// Ã¿Ò»Åú´ÎµÄÊµÀýÊýÁ¿ÉÏÏÞ£¨Ì«¶àÓÐÐ©Éè±¸»áÓÐÒì³££©
    /// </summary>
    public const int INSTANCE_COUNT_PER_BATCH = 200;

    public Camera uiCamera;
    /// <summary>
    /// ÃÉ°æÌùÍ¼
    /// </summary>
    public Image maskImage;
    /// <summary>
    /// ±ÊË¢ÌùÍ¼
    /// </summary>
    public Texture2D brushTex;

    /// <summary>
    /// ±ÊË¢³ß´ç
    /// </summary>
    [Range(1f, 200f)]
    public float brushSize = 50f;
    /// <summary>
    /// »æÖÆ²½½ø¾«¶È(Öµ¹ý´ó»á±ä³ÉµãÁ´£¬¹ýÐ¡ÔòÓÐÐÔÄÜÑ¹Á¦)
    /// TODO ¸Ä³É¸ù¾ÝbrushSize×Ô¶¯¼ÆËã
    /// </summary>
    [Range(1f, 20f)]
    public float paintStep = 5f;
    /// <summary>
    /// ±ÊË¢ÒÆ¶¯¼ì²âãÐÖµ
    /// </summary>
    [Range(1f, 10f)]
    public float moveThreshhold = 2f;
    /// <summary>
    /// ±ÊË¢²»Í¸Ã÷¶È
    /// </summary>
    [Range(0f, 1f)]
    public float brushAlpha = 1f;
    /// <summary>
    /// »æÍ¼²ÄÖÊ
    /// </summary>
    public Material paintMaterial;
    /// <summary>
    /// ÓÃÀ´Éú³ÉÖ±·½Í¼Êý¾ÝµÄshader
    /// </summary>

    /// <summary>
    /// Ö±·½Í¼Êý¾Ý
    /// </summary>
    private uint[]          _histogramData;
    private ComputeBuffer   _histogramBuffer;
    private int             _clearShaderKrnl;
    private int             _histogramShaderKrnl;
    private Vector2Int      _histogramShaderGroupSize;

    private RenderTexture   _rt;
    private CommandBuffer   _cb;
    private bool            _isDirty;
    private Vector2         _beginPos;
    private Vector2         _endPos;
    
    private Mesh            _quad;
    private Matrix4x4       _matrixProj;
    private Matrix4x4[]     _arrInstancingMatrixs;

    private int             _propIDMainTex;
    private int             _propIDBrushAlpha;
    private Vector2         _lastPoint;
    private Vector2         _maskSize;

    public Vector2 rtSize => new Vector2(_rt.width, _rt.height);


    /// <summary>
    /// ÖØÖÃÃÉ°æ
    /// </summary>
    public void ResetMask()
    {
        SetupPaintContext(true);
        Graphics.ExecuteCommandBuffer(_cb);
        _isDirty = false;
    }

    /// <summary>
    /// »ñÈ¡¹Î¿ªµÄÍ³¼ÆÐÅÏ¢
    /// </summary>
    /// <returns></returns>
    public StatData GetStatData()
    {
        if (_histogramShaderKrnl == -1)
        {
            Debug.LogError("invalid compute shader");
            return new StatData();
        }


        int dispatchX = _rt.width / _histogramShaderGroupSize.x;
        int dispatchY = _rt.height / _histogramShaderGroupSize.y;

        // AsyncGPUReadback.Request does supported at OpenglES
        _histogramBuffer.GetData(_histogramData);

        int dispatchWidth = dispatchX * _histogramShaderGroupSize.x;
        int dispatchHeight = dispatchY * _histogramShaderGroupSize.y;
        int dispatchCount = dispatchWidth * dispatchHeight;

        StatData ret = new StatData();
        ret.fillPercent = 1.0f - _histogramData[0] / (dispatchCount * 1.0f); // ·Ç0Öµ±ÈÀý

        float sum = 0;
        float binScale = (256 / HISTOGRAM_BINS);
        for (int i = 0; i < HISTOGRAM_BINS; i++)
        {
            int count = (int)_histogramData[i];
            sum += i * binScale * count;
        }
        ret.avgVal = sum / dispatchCount;
        // ÓÉÓÚÍ°µÄÊýÁ¿Ð¡ÓÚ256£¬shader×î´óÖ»Í³¼Æµ½ 127 * 2 = 254, ÎÞ·¨ÏÔÊ¾255µÄÊý¾Ý£¬Òò´Ë´Ë´¦°Ñ½á¹û¸øËõ·ÅÒ»ÏÂ
        ret.avgVal *= 255.0f / ((HISTOGRAM_BINS - 1) * binScale);
        return ret;
    }

    void Start()
    {
        Init();
        ResetMask();
    }

    private void OnDestroy()
    {
        if(_rt != null)
            Destroy(_rt);

        if(_quad != null)
            Destroy(_quad);

        if (_cb != null)
            _cb.Dispose();

        if (_histogramBuffer != null)
            _histogramBuffer.Release();
    }

    private void Update()
    {
        CheckInput();
    }

    void LateUpdate()
    {
        if(BuildCommands())
        {
            Graphics.ExecuteCommandBuffer(_cb);
            _beginPos = _endPos;
        }
    }

    private bool BuildCommands()
    {
        if (!_isDirty)
            return false;

        paintMaterial.SetTexture(_propIDMainTex, brushTex != null ? brushTex : Texture2D.whiteTexture);
        paintMaterial.SetFloat(_propIDBrushAlpha, brushAlpha);

        Vector2 fromToVec = _endPos - _beginPos;
        Vector2 dir = fromToVec.normalized;
        float len = fromToVec.magnitude;

        float offset = 0;
        int instCount = 0;

        SetupPaintContext(false);

        while (offset <= len)
        {
            if (instCount >= INSTANCE_COUNT_PER_BATCH)
            {
                _cb.DrawMeshInstanced(_quad, 0, paintMaterial, 0, _arrInstancingMatrixs, instCount);
                instCount = 0;
            }

            Vector2 tmpPt = _beginPos + dir * offset;
            tmpPt -= Vector2.one * brushSize * 0.5f; // ½«±ÊË¢¾ÓÖÐµ½»æÖÆµã
            offset += paintStep;

            _arrInstancingMatrixs[instCount++] = Matrix4x4.TRS(new Vector3(tmpPt.x, tmpPt.y, 0), Quaternion.identity, Vector3.one * brushSize);
        }

        if(instCount > 0)
        {
            _cb.DrawMeshInstanced(_quad, 0, paintMaterial, 0, _arrInstancingMatrixs, instCount);
        }

        _isDirty = false;
        return true;
    }

    private void Init()
    {
        _lastPoint = Vector2.zero;

        _quad = new Mesh();
        _quad.SetVertices(new Vector3[]
        {
            new Vector3(0, 0, 0),
            new Vector3(0, 1, 0),
            new Vector3(1, 0, 0),
            new Vector3(1, 1, 0)
        });

        _quad.SetUVs(0, new Vector2[]
            {
                new Vector2(0, 0),
                new Vector2(0, 1),
                new Vector2(1, 0),
                new Vector2(1, 1)
            });

        _quad.SetIndices(new int[] { 0, 1, 2, 3, 2, 1 }, MeshTopology.Triangles, 0, false);
        _quad.UploadMeshData(true);


        _maskSize = maskImage.rectTransform.rect.size;
        //Debug.LogFormat("mask image size:{0}*{1}", maskSize.x, maskSize.y);

        _rt = new RenderTexture((int)(_maskSize.x * ALPHA_RT_SCALE), (int)(_maskSize.y * ALPHA_RT_SCALE), 0, RenderTextureFormat.R8, 0);
        _rt.antiAliasing = 2;
        _rt.autoGenerateMips = false;

        _arrInstancingMatrixs = new Matrix4x4[INSTANCE_COUNT_PER_BATCH];
        _matrixProj = Matrix4x4.Ortho(0, _maskSize.x, 0, _maskSize.y, -1f, 1f);

        _propIDMainTex = Shader.PropertyToID("_MainTex");
        _propIDBrushAlpha = Shader.PropertyToID("_BrushAlpha");

        paintMaterial.enableInstancing = true;

        Material maskMat = maskImage.material;
        maskMat.SetTexture("_AlphaTex", _rt);

        _cb = new CommandBuffer() { name = "PaintOncb" };

        // setup histogram compute shader
        _clearShaderKrnl = -1;
    }

    private void SetupPaintContext(bool clearRT)
    {
        _cb.Clear();
        _cb.SetRenderTarget(_rt);

        if (clearRT)
        {
            _cb.ClearRenderTarget(true, true, Color.clear);
        }

        _cb.SetViewProjectionMatrices(Matrix4x4.identity, _matrixProj);
    }

    private void CheckInput()
    {
        if (uiCamera == null)
            return;

        int mouseStatus = 0;// 0£ºnone, 1:down, 2:hold, 3:up

        if (Input.GetMouseButtonDown(0)) // °´ÏÂÊó±ê
            mouseStatus = 1;
        else if (Input.GetMouseButton(0)) // ÒÆ¶¯Êó±ê»òÕß´¦ÓÚ°´ÏÂ×´Ì¬
            mouseStatus = 2;
        else if (Input.GetMouseButtonUp(0)) // ÊÍ·ÅÊó±ê
            mouseStatus = 3;

        if (mouseStatus == 0)
            return;

        Vector2 localPt = Vector2.zero;
        RectTransformUtility.ScreenPointToLocalPointInRectangle(transform as RectTransform, Input.mousePosition, uiCamera, out localPt);

        //Debug.Log($"pt:{localPt}, status:{mouseStatus}");

        if (localPt.x < 0 || localPt.y < 0 || localPt.y >= _maskSize.x || localPt.y >= _maskSize.y)
            return;

        switch (mouseStatus)
        {
            case 1:
                _beginPos = localPt;
                _lastPoint = localPt;
                break;
            case 2:
                if (Vector2.Distance(localPt, _lastPoint) > moveThreshhold)
                {
                    _endPos = localPt;
                    _lastPoint = localPt;
                    _isDirty = true;
                }
                break;
            case 3:
                _endPos = localPt;
                _lastPoint = localPt;
                _isDirty = true;
                break;
        }
    }
}
