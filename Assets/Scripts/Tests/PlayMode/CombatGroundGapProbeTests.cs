using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using NUnit.Framework;
using Roguelike.Features.Combat.Runtime;
using Roguelike.Features.Combat.Run;
using Roguelike.Infrastructure.Configuration;
using Roguelike.Infrastructure.Resources;
using UnityEngine;
using UnityEngine.TestTools;

namespace Roguelike.Tests
{
    public sealed partial class CombatRuntimeTests
    {
        /// <summary>明确选择时启动无Profile的独立试炼，正常连续移动并采集真实屏幕左边缘及地面覆盖；只诊断不修复。</summary>
        /// <returns>正式资源加载、连续移动和帧末像素读回的协程。</returns>
        /// <remarks>不暂停编辑器、不改相机/HUD/材质/地图，不读取真实档案；所有截图为测试证据，结束释放本局与资源。</remarks>
        [UnityTest,Explicit("用户授权的隔离运行时地面缝隙诊断，单独经MCP运行。"),Timeout(180000)]
        public IEnumerator CaptureContinuousLeftEdgeGroundGapEvidence()
        {
#if UNITY_EDITOR
            var priorFocus=UnityEditor.EditorWindow.focusedWindow;
            UnityEditor.EditorWindow.GetWindow(typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.GameView")).Focus();
#endif
            using var initializer=new YooAssetPackageInitializer();yield return Wait(initializer.InitializeAsync(ResourcePlayMode.EditorSimulate,CancellationToken.None));
            var config=new LubanConfigService(initializer);yield return Wait(config.InitializeAsync(CancellationToken.None));
            var resources=new YooAssetResourceService(initializer,config);var definition=CombatRunDefinition.Create(config.Tables,101);
            var root=new GameObject("Isolated ground gap visual probe");var runner=root.AddComponent<CombatRuntimeRunner>();var input=new TestInput();
            string dir="outputs/combat-config/ground-gap-20261028/window-compare";Directory.CreateDirectory(dir);
            var report=new List<string>{"sample,frame,seconds,dt,tick,backlog,px,py,cx,cy,screenW,screenH,leftDarkPrefix,clearColorPixelCount,groundLeftMargin,groundRightMargin,groundBottomMargin,groundTopMargin,borderScreenX,state"};
            Color32[] first=null,last=null,previousStrip=null,pairBefore=null,pairAfter=null;int width=0,height=0,stripWidth=0,bestChange=-1,pairIndex=-1;string reason="time-limit";
            var windowFrames=new List<Color32[]>();var windowMetadata=new List<string>();int windowWidth=0,windowHeight=0;
            try
            {
                yield return Wait(runner.InitializeAsync(definition,resources,input,CancellationToken.None));
                Assert.That(runner.Ready,Is.True);Debug.Log("GROUND_GAP_PROBE_STARTED stage=101 profile=none continuous=true");
                double began=Time.realtimeSinceStartupAsDouble;int index=0,previousPrefix=-1;
                while(Time.realtimeSinceStartupAsDouble-began<12 && index<600)
                {
                    double age=Time.realtimeSinceStartupAsDouble-began;
                    input.Frame=new CombatInputFrame{Movement=age<6?new Unity.Mathematics.float2(-1,0):age<9?new Unity.Mathematics.float2(-1,1):new Unity.Mathematics.float2(-1,-1)};
                    yield return null;yield return new WaitForEndOfFrame();
                    Texture2D capture=ScreenCapture.CaptureScreenshotAsTexture();
                    try
                    {
                        width=capture.width;height=capture.height;stripWidth=Math.Min(256,width);var pixels=capture.GetPixels32();
                        if(first==null)first=pixels;last=pixels;
                        var strip=new Color32[stripWidth*height];for(int y=0;y<height;y++)Array.Copy(pixels,y*width,strip,y*stripWidth,stripWidth);
                        int prefix=0,row=height/2;while(prefix<stripWidth&&IsGroundGapDark(pixels[row*width+prefix]))prefix++;
                        int clear=0;Color bg=runner.ViewCamera.backgroundColor;
                        for(int y=height/3;y<height*2/3;y++)for(int x=0;x<stripWidth;x++){var c=pixels[y*width+x];if(Math.Abs(c.r-bg.r*255)<5&&Math.Abs(c.g-bg.g*255)<5&&Math.Abs(c.b-bg.b*255)<5)clear++;}
                        int change=previousPrefix<0?-1:Math.Abs(prefix-previousPrefix);
                        if(previousStrip!=null&&change>bestChange){bestChange=change;pairBefore=previousStrip;pairAfter=strip;pairIndex=index;}
                        previousStrip=strip;previousPrefix=prefix;
                        var camera=runner.ViewCamera;var bounds=runner.MapBackground.Tilemap.GetComponent<UnityEngine.Tilemaps.TilemapRenderer>().bounds;
                        float distance=bounds.center.z-camera.transform.position.z;
                        var lo=camera.ViewportToWorldPoint(new Vector3(0,0,distance));var hi=camera.ViewportToWorldPoint(new Vector3(1,1,distance));
                        var player=runner.Session.ReadUnit(0);var pos=camera.transform.position;
                        float border=camera.WorldToScreenPoint(new Vector3(-runner.Session.MovementBoundsHalfSize.x,pos.y,0)).x;
                        report.Add(string.Join(",",new double[]{index,Time.frameCount,Time.realtimeSinceStartupAsDouble-began,Time.unscaledDeltaTime,runner.Session.Statistics.Tick,runner.Session.BacklogSeconds,player.Position.x,player.Position.y,pos.x,pos.y,width,height,prefix,clear,lo.x-bounds.min.x,bounds.max.x-hi.x,lo.y-bounds.min.y,bounds.max.y-hi.y,border}.Select(v=>v.ToString("R",CultureInfo.InvariantCulture)))+","+runner.RunModel.State);
#if UNITY_EDITOR
                        if(age>=6&&windowFrames.Count<2)
                        {
                            string metadata;windowFrames.Add(CaptureGroundGapGameWindow(out windowWidth,out windowHeight,out metadata));
                            windowMetadata.Add("sample="+index+" "+metadata);
                        }
#endif
                    }
                    finally{UnityEngine.Object.Destroy(capture);}
                    index++;
                    if(runner.RunModel.State!=CombatRunState.Playing){reason="state-"+runner.RunModel.State;break;}
                    if(runner.Session.BacklogSeconds>.5){reason="capture-backlog-guard";break;}
                }
                input.Frame=default;
                Assert.That(first,Is.Not.Null,"The probe must capture actual rendered frames.");
                WriteGroundGapPixels(Path.Combine(dir,"first-full.png"),first,width,height);WriteGroundGapPixels(Path.Combine(dir,"last-full.png"),last,width,height);
                if(pairBefore!=null){WriteGroundGapPixels(Path.Combine(dir,"adjacent-before-left.png"),pairBefore,stripWidth,height);WriteGroundGapPixels(Path.Combine(dir,"adjacent-after-left.png"),pairAfter,stripWidth,height);}
                for(int n=0;n<windowFrames.Count;n++)WriteGroundGapPixels(Path.Combine(dir,"game-window-"+n+".png"),windowFrames[n],windowWidth,windowHeight);
                File.WriteAllLines(Path.Combine(dir,"window-metadata.txt"),windowMetadata);
                File.WriteAllText(Path.Combine(dir,"summary.txt"),"reason="+reason+"\nsamples="+index+"\nmaxAdjacentDarkPrefixChange="+bestChange+"\npairAfterSample="+pairIndex+"\nprofile=none\n");
                Debug.Log("GROUND_GAP_PROBE_FINISHED samples="+index+" reason="+reason+" adjacentChange="+bestChange);
            }
            finally
            {
                input.Frame=default;File.WriteAllLines(Path.Combine(dir,"frames.csv"),report);runner.Close();UnityEngine.Object.Destroy(root);
#if UNITY_EDITOR
                if(priorFocus!=null)priorFocus.Focus();
#endif
            }
            yield return null;
        }

        /// <summary>识别中央扫描行近黑区域以选择最值得复核的相邻帧，不把此阈值当成缝隙根因结论。</summary>
        /// <param name="pixel">实际屏幕像素。</param><returns>三个通道均接近暗背景时为真。</returns>
        private static bool IsGroundGapDark(Color32 pixel)=>pixel.r<35&&pixel.g<35&&pixel.b<35;

#if UNITY_EDITOR
        /// <summary>只读当前已核验Unity实例的Game窗口像素，同时保留窗口外框与游戏渲染尺寸，区分视口外留白。</summary>
        /// <param name="width">窗口像素宽度。</param><param name="height">窗口像素高度。</param><param name="metadata">窗口范围与游戏目标尺寸。</param>
        /// <returns>仅该Game窗口的像素，不读取其他应用窗口。</returns>
        /// <remarks>由MCP启动的隔离测试在帧末调用；不更改缩放、尺寸、相机或任何设置。</remarks>
        private static Color32[] CaptureGroundGapGameWindow(out int width,out int height,out string metadata)
        {
            var view=UnityEditor.EditorWindow.GetWindow(typeof(UnityEditor.EditorWindow).Assembly.GetType("UnityEditor.GameView"));view.Focus();
            Rect rect=view.position;float dpi=UnityEditor.EditorGUIUtility.pixelsPerPoint;width=Mathf.RoundToInt(rect.width*dpi);height=Mathf.RoundToInt(rect.height*dpi);
            var colors=UnityEditorInternal.InternalEditorUtility.ReadScreenPixel(new Vector2(rect.x*dpi,rect.y*dpi),width,height);
            metadata="window="+rect+" dpi="+dpi+" target="+Screen.width+"x"+Screen.height;
            var result=new Color32[colors.Length];for(int i=0;i<colors.Length;i++)result[i]=colors[i];return result;
        }
#endif

        /// <summary>采样结束后才把证据像素编码为PNG，避免编码阻塞连续运行帧。</summary>
        /// <param name="path">本用例输出路径。</param><param name="pixels">已读回像素。</param><param name="width">像素宽度。</param><param name="height">像素高度。</param>
        /// <remarks>只保存验收截图；临时纹理立即销毁，不生成Unity预览资产。</remarks>
        private static void WriteGroundGapPixels(string path,Color32[] pixels,int width,int height)
        {var texture=new Texture2D(width,height,TextureFormat.RGB24,false);try{texture.SetPixels32(pixels);texture.Apply();File.WriteAllBytes(path,texture.EncodeToPNG());}finally{UnityEngine.Object.Destroy(texture);}}
    }
}
