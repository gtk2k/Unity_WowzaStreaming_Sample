using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;
using WebSocketSharp;
using Unity.WebRTC;
using System.Linq;

public class WowzaConnector : MonoBehaviour
{
    [SerializeField] private string sdpURL;
    [SerializeField] private string applicationName;
    [SerializeField] private string streamName;

    private WebSocket ws;
    private SynchronizationContext ctx;
    private RenderTexture rt;
    private Camera cam;
    private RTCPeerConnection pc;

    private RTCConfiguration config = new RTCConfiguration
    {
        iceServers = new[] { new RTCIceServer { urls = new[] { "stun:stun.l.googl.com:19302" } } }
    };

    [Serializable]
    private class WowzaStreamInfo
    {
        public string applicationName;
        public string streamName;
        public string sessionId;
    }

    [Serializable]
    private class WowzaSDP
    {
        public string type;
        public string sdp;
    }

    [Serializable]
    private class WowzaIceCandidate
    {
        public string candidate;
        public string sdpMid;
        public int sdpMLineIndex = 0;
    }

    [Serializable]
    private class WowzaSignalingMessage
    {
        public int status = 0;
        public string statusDescription = null;
        public string direction = null;
        public string command = null;
        public WowzaStreamInfo streamInfo = null;
        public WowzaSDP sdp = null;
        public WowzaIceCandidate[] iceCandidates = null;

        public RTCSessionDescription ToDesc()
        {
            return new RTCSessionDescription
            {
                type = sdp.type == "offer" ? RTCSdpType.Offer : RTCSdpType.Answer,
                sdp = sdp.sdp
            };
        }

        public RTCIceCandidate[] ToCands()
        {
            if (iceCandidates == null) return null;
            return iceCandidates.Select(c =>
            {
                return new RTCIceCandidate(new RTCIceCandidateInit
                {
                    candidate = c.candidate,
                    sdpMid = c.sdpMid,
                    sdpMLineIndex = c.sdpMLineIndex
                });
            }).ToArray();
        }
    }

    private enum Side
    {
        Local,
        Remote
    }

    [Flags]
    public enum SslProtocols
    {
        None = 0,
        Ssl2 = 12,
        Ssl3 = 48,
        Tls = 192,
        Default = 240,
        Tls11 = 768,
        Tls12 = 3072
    }

    // Start is called before the first frame update
    void Start()
    {
        WebRTC.Initialize();
        StartCoroutine(WebRTC.Update());

        ctx = SynchronizationContext.Current;
        cam = Camera.main;
        rt = new RenderTexture(1920, 1080, 0, RenderTextureFormat.BGRA32, 0);

        ws = new WebSocket(sdpURL);
        ws.OnOpen += Ws_OnOpen;
        ws.OnMessage += Ws_OnMessage;
        ws.OnClose += Ws_OnClose;
        ws.OnError += Ws_OnError;
        ws.Connect();
    }

    public void StartStreaming()
    {
        Debug.Log($"StartStreaming");

        pc = new RTCPeerConnection(ref config);

        pc.OnIceCandidate = cand =>
        {
            pc.OnIceCandidate = null;
            var msg = CreateSignalingMessage(pc.LocalDescription);
            SendSignalingMessage(msg);
        };

        pc.OnIceGatheringStateChange = state =>
        {
            Debug.Log($"OnIceGatheringStateChange > state: {state}");
        };

        pc.OnConnectionStateChange = state =>
        {
            Debug.Log($"OnConnectionStateChange > state: {state}");
        };

        var videoTrack = new VideoStreamTrack(rt);
        pc.AddTrack(videoTrack);
        StartCoroutine(CreateDesc(RTCSdpType.Offer));
    }

    private void Ws_OnOpen(object sender, System.EventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.Log($"WS OnOpen");
            StartStreaming();
        }, null);
    }

    private void Ws_OnMessage(object sender, MessageEventArgs e)
    {
        ctx.Post(_ =>
        {
            var msg = JsonUtility.FromJson<WowzaSignalingMessage>(e.Data);
            var desc = msg.ToDesc();
            var candidates = msg.ToCands();

            StartCoroutine(SetDesc(Side.Remote, desc, candidates));
        }, null);
    }

    private void Ws_OnClose(object sender, CloseEventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.Log($"WS OnClose > code:{e.Code}, reason:{e.Reason}");
        }, null);
    }

    private void Ws_OnError(object sender, ErrorEventArgs e)
    {
        ctx.Post(_ =>
        {
            Debug.LogError($"WS OnError: {e.Message}");
        }, null);
    }

    private IEnumerator CreateDesc(RTCSdpType type)
    {
        var op = type == RTCSdpType.Offer ? pc.CreateOffer() : pc.CreateAnswer();
        yield return op;
        if (op.IsError)
        {
            Debug.LogError($"Create {type} Error: {op.Error.message}");
            yield break;
        }

        StartCoroutine(SetDesc(Side.Local, op.Desc));
    }

    private IEnumerator SetDesc(Side side, RTCSessionDescription desc, RTCIceCandidate[] cands = null)
    {
        var op = side == Side.Local ? pc.SetLocalDescription(ref desc) : pc.SetRemoteDescription(ref desc);
        yield return op;
        if (op.IsError)
        {
            Debug.LogError($"Set {desc.type} Error: {op.Error.message}");
            yield break;
        }

        if (cands != null)
        {
            for (int i = 0; i < cands.Length; i++)
            {
                pc.AddIceCandidate(cands[i]);
            }
        }
    }

    private WowzaSignalingMessage CreateSignalingMessage(RTCSessionDescription desc)
    {
        return new WowzaSignalingMessage
        {
            direction = "publish",
            command = "sendOffer",
            streamInfo = new WowzaStreamInfo
            {
                applicationName = applicationName,
                streamName = streamName,
                sessionId = "[empty]"
            },
            sdp = new WowzaSDP
            {
                type = desc.type == RTCSdpType.Offer ? "offer" : "answer",
                sdp = desc.sdp
            }
        };
    }

    private void SendSignalingMessage(WowzaSignalingMessage msg)
    {
        var jsonStr = JsonUtility.ToJson(msg, true);
        Debug.Log($"Send Signaling");
        Debug.Log(jsonStr);
        ws.Send(jsonStr);
    }

    // Update is called once per frame
    void Update()
    {
        var defaultTargetTexture = cam.targetTexture;
        cam.targetTexture = rt;
        cam.Render();
        cam.targetTexture = defaultTargetTexture;
    }
}
