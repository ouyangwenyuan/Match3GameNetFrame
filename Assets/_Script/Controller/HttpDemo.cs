using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

public class HttpDemo : MonoBehaviour
{
    public Button btnLogin;
    public Button btnSyncProgress;
    public Button btnJoinRoom;
    public Button btnAuthenticate;
    public Text txtLogin;
    public Text txtMsg;

    // 在任意MonoBehaviour中调用
    void Start()
    {
        NetworkManager.Instance.Startup();
        Messenger.AddListener<object>("prop collected", OnWebSocketMessage);

        btnLogin.onClick.AddListener(() =>
        {
            // 玩家登录
            var loginData = "{\"player_id\":\"player1\",\"player_name\":\"Test Player\"}";
            NetworkManager.Instance.SendRequest("/api/player/login", response =>
            {
                Debug.Log("Login response: " + response.Data);
            }, null, loginData);


        });

        btnSyncProgress.onClick.AddListener(() =>
        {
            // 获取玩家数据
            NetworkManager.Instance.SendRequest("/api/player/data", response =>
            {
                Debug.Log("Player data: " + response.Data);
            }, new Dictionary<string, object> { { "player_id", "player1" } });

        });

        btnAuthenticate.onClick.AddListener(() =>
        {
          
            // 连接WebSocket
            // 认证
            var authMessage = new WebSocketMessage
            {
                type = "authenticate",
                data = "{\"player_id\":\"player1\",\"auth_token\":\"your-auth-token\"}"
            };
            NetworkManager.Instance.SendWebSocketMessage(JsonUtility.ToJson(authMessage), response =>
            {
                Debug.Log("Auth response: " + response.Data);
            });
        });


        btnJoinRoom.onClick.AddListener(() =>
        {

            // 加入房间
            var joinMessage = new WebSocketMessage
            {
                type = "join_room",
                data = "{\"room_id\":\"test-room\"}"
            };
            NetworkManager.Instance.SendWebSocketMessage(JsonUtility.ToJson(joinMessage), response =>
            {
                Debug.Log("Join room response: " + response.Data);
            });
        });
    }

    private void OnDestroy()
    {
        Messenger.RemoveListener<object>("prop collected", OnWebSocketMessage);
    }


    private void OnWebSocketMessage(object data)
    {
        var response = (NetworkResponse)data;
        if (response.Success)
        {
            var message = JsonUtility.FromJson<WebSocketMessage>(response.Data);

            switch (message.type)
            {
                case "player_joined":
                    // 处理新玩家加入
                    break;
                case "game_start":
                    // 处理游戏开始
                    break;
            }
        }
    }
}