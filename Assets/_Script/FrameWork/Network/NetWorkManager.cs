using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Networking;
using WebSocketSharp;

public enum NetworkRequestPriority
{
    Low = 0,
    Normal = 1,
    High = 2
}

public enum NetworkErrorType
{
    None,
    Timeout,
    NoInternet,
    ServerError,
    DataParsingError,
    AuthenticationError
}

public class NetworkResponse
{
    public bool Success { get; set; }
    public string Data { get; set; }
    public NetworkErrorType ErrorType { get; set; }
    public string ErrorMessage { get; set; }
    public long ResponseCode { get; set; }
}

public interface INetworkSecurity
{
    string Encrypt(string data);
    string Decrypt(string data);
    string GenerateSign(Dictionary<string, object> parameters);
}

public class DefaultSecurity : INetworkSecurity
{
    private string _secretKey;
    
    public DefaultSecurity(string secretKey)
    {
        _secretKey = secretKey;
    }
    
    public string Encrypt(string data)
    {
        // 实际项目中使用AES等加密算法
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(data));
    }

    public string Decrypt(string data)
    {
        try
        {
            byte[] encryptedBytes = Convert.FromBase64String(data);
            return Encoding.UTF8.GetString(encryptedBytes);
        }
        catch
        {
            return string.Empty;
        }
    }

    public string GenerateSign(Dictionary<string, object> parameters)
    {
        // 简化的签名算法，实际项目应使用更安全的方案
        var sb = new StringBuilder();
        foreach (var kvp in parameters)
        {
            sb.Append($"{kvp.Key}={kvp.Value}&");
        }
        sb.Append(_secretKey);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(sb.ToString()));
    }
}

public class NetworkManager : MonoSingleton<NetworkManager>
{
    // private static NetworkManager _instance;
    // public static NetworkManager Instance => _instance;

    [Header("Base Configuration")]
    [SerializeField] private string _apiBaseUrl = "http://127.0.0.1:5000";
    [SerializeField] private string _webSocketUrl = "ws://127.0.0.1:5000";
    [SerializeField] private string _secretKey = "your-secret-key";
    [SerializeField] private float _requestTimeout = 10f;
    [SerializeField] private int _maxRetryCount = 3;

    [Header("WebSocket Configuration")]
    [SerializeField] private float _webSocketReconnectInterval = 5f;
    [SerializeField] private float _webSocketHeartbeatInterval = 30f;

    private INetworkSecurity _security;
    private Queue<Action> _requestQueue = new Queue<Action>();
    private bool _isProcessingQueue = false;
    private WebSocket _webSocket;
    private float _lastWebSocketMessageTime;
    private bool _webSocketConnected = false;
    private Coroutine _reconnectCoroutine;
    private Dictionary<string, Action<NetworkResponse>> _webSocketCallbacks = new Dictionary<string, Action<NetworkResponse>>();


    override protected void Init()
    {
        InitializeWebSocket();
        _security = new DefaultSecurity(_secretKey);

    }
    override public void Dispose()
    {

    }

    private void Update()
    {
        ProcessRequestQueue();

        // WebSocket心跳检测
        if (_webSocketConnected && Time.time - _lastWebSocketMessageTime > _webSocketHeartbeatInterval)
        {
            SendWebSocketMessage("{\"type\":\"heartbeat\"}");
        }
    }

    private void OnDestroy()
    {
        if (_webSocket != null)
        {
            _webSocket.Close();
            _webSocket = null;
        }
    }

    #region HTTP Request Management

    public void SendRequest(string endpoint, 
                           Action<NetworkResponse> callback,
                           Dictionary<string, object> parameters = null,
                           string postData = null,
                           NetworkRequestPriority priority = NetworkRequestPriority.Normal)
    {
        void RequestAction()
        {
            StartCoroutine(SendRequestCoroutine(endpoint, callback, parameters, postData));
        }

        lock (_requestQueue)
        {
            if (priority == NetworkRequestPriority.High)
            {
                var tempQueue = new Queue<Action>();
                tempQueue.Enqueue(RequestAction);
                while (_requestQueue.Count > 0)
                {
                    tempQueue.Enqueue(_requestQueue.Dequeue());
                }
                _requestQueue = tempQueue;
            }
            else
            {
                _requestQueue.Enqueue(RequestAction);
            }
        }
    }

    private void ProcessRequestQueue()
    {
        if (_isProcessingQueue || _requestQueue.Count == 0) return;

        _isProcessingQueue = true;
        Action requestAction = _requestQueue.Dequeue();

        try
        {
            requestAction?.Invoke();
        }
        finally
        {
            _isProcessingQueue = false;
        }
    }

    private IEnumerator SendRequestCoroutine(string endpoint, 
                                           Action<NetworkResponse> callback,
                                           Dictionary<string, object> parameters,
                                           string postData,
                                           int retryCount = 0)
    {
        // 添加公共参数和签名
        if (parameters == null) parameters = new Dictionary<string, object>();
        parameters["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        parameters["nonce"] = Guid.NewGuid().ToString();
        parameters["sign"] = _security.GenerateSign(parameters);

        // 构建URL
        var urlBuilder = new StringBuilder(_apiBaseUrl).Append(endpoint);
        bool firstParam = true;
        foreach (var kvp in parameters)
        {
            urlBuilder.Append(firstParam ? "?" : "&")
                     .Append(Uri.EscapeDataString(kvp.Key))
                     .Append("=")
                     .Append(Uri.EscapeDataString(kvp.Value.ToString()));
            firstParam = false;
        }

        string url = urlBuilder.ToString();
        UnityWebRequest request;

        if (string.IsNullOrEmpty(postData))
        {
            request = UnityWebRequest.Get(url);
        }
        else
        {
            // 加密POST数据
            string encryptedData = _security.Encrypt(postData);
            request = UnityWebRequest.Post(url, encryptedData);
            request.uploadHandler = new UploadHandlerRaw(Encoding.UTF8.GetBytes(encryptedData));
            request.SetRequestHeader("Content-Type", "application/json");
        }

        request.timeout = (int)_requestTimeout;
        
        // 添加自定义请求头
        request.SetRequestHeader("X-Game-Version", Application.version);
        request.SetRequestHeader("X-Platform", Application.platform.ToString());

        yield return request.SendWebRequest();

        var response = new NetworkResponse
        {
            ResponseCode = request.responseCode
        };

        if (request.result == UnityWebRequest.Result.Success)
        {
            try
            {
                string decryptedData = _security.Decrypt(request.downloadHandler.text);
                response.Success = true;
                response.Data = decryptedData;
            }
            catch (Exception e)
            {
                response.Success = false;
                response.ErrorType = NetworkErrorType.DataParsingError;
                response.ErrorMessage = $"Data decryption failed: {e.Message}";
            }
        }
        else
        {
            response.Success = false;
            
            if (request.result == UnityWebRequest.Result.ConnectionError)
            response.ErrorType = NetworkErrorType.NoInternet;
            else if (request.result == UnityWebRequest.Result.ProtocolError)
                response.ErrorType = NetworkErrorType.ServerError;
            else if (request.result == UnityWebRequest.Result.DataProcessingError)
                response.ErrorType = NetworkErrorType.DataParsingError;

            response.ErrorMessage = request.error;

            // 自动重试逻辑
            if (retryCount < _maxRetryCount && 
                (request.responseCode == 0 || request.responseCode >= 500))
            {
                yield return new WaitForSeconds(1 * (retryCount + 1));
                yield return SendRequestCoroutine(endpoint, callback, parameters, postData, retryCount + 1);
                yield break;
            }
        }

        callback?.Invoke(response);
        request.Dispose();
    }

    #endregion

    #region WebSocket Management

    private void InitializeWebSocket()
    {
        if (_webSocket != null)
        return;

        _webSocket = new WebSocket(_webSocketUrl);
        
        // 添加安全协议头
        _webSocket.SetCredentials("game_token", GetAuthToken(), true);
        
        _webSocket.OnOpen += (sender, e) =>
        {
            Debug.Log("WebSocket connected");
            _webSocketConnected = true;
            _lastWebSocketMessageTime = Time.time;
            
            if (_reconnectCoroutine != null)
            {
                StopCoroutine(_reconnectCoroutine);
                _reconnectCoroutine = null;
            }
        };

        _webSocket.OnMessage += (sender, e) =>
        {
            _lastWebSocketMessageTime = Time.time;
            
            try
            {
                string decryptedData = _security.Decrypt(e.Data);
                var response = JsonUtility.FromJson<NetworkResponse>(decryptedData);
                
                // 处理回调
                if (!string.IsNullOrEmpty(response.Data))
                {
                    var dataObj = JsonUtility.FromJson<WebSocketMessage>(response.Data);
                    if (dataObj != null && !string.IsNullOrEmpty(dataObj.callbackId) && 
                        _webSocketCallbacks.ContainsKey(dataObj.callbackId))
                    {
                        _webSocketCallbacks[dataObj.callbackId]?.Invoke(response);
                        _webSocketCallbacks.Remove(dataObj.callbackId);
                    }
                }
                
                // 触发全局事件
                // EventManager.Instance.TriggerEvent("WebSocketMessage", response);
            }
            catch (Exception ex)
            {
                Debug.LogError($"WebSocket message processing error: {ex.Message}");
            }
        };

        _webSocket.OnError += (sender, e) =>
        {
            Debug.LogError($"WebSocket error: {e.Message}");
            HandleWebSocketDisconnection();
        };

        _webSocket.OnClose += (sender, e) =>
        {
            Debug.Log($"WebSocket closed: {e.Reason}");
            HandleWebSocketDisconnection();
        };

        ConnectWebSocket();
    }

    private void ConnectWebSocket()
    {
        if (_webSocket == null || _webSocket.ReadyState == WebSocketState.Open)
        {
            Debug.Log($"WebSocket is null");
            return;
        }

        try
        {
            _webSocket.ConnectAsync();
            Debug.Log($"WebSocket connect async");
        }
        catch (Exception e)
        {
            Debug.LogError($"WebSocket connect error: {e.Message}");
            HandleWebSocketDisconnection();
        }
    }

    private void HandleWebSocketDisconnection()
    {
        _webSocketConnected = false;
        
        if (_reconnectCoroutine == null)
        {
            _reconnectCoroutine = StartCoroutine(ReconnectWebSocket());
        }
    }

    private IEnumerator ReconnectWebSocket()
    {
        while (!_webSocketConnected)
        {
            yield return new WaitForSeconds(_webSocketReconnectInterval);
            Debug.Log("Attempting to reconnect WebSocket...");
            ConnectWebSocket();
        }
    }

    public void SendWebSocketMessage(string message, Action<NetworkResponse> callback = null)
    {
        if (!_webSocketConnected)
        {
            callback?.Invoke(new NetworkResponse
            {
                Success = false,
                ErrorType = NetworkErrorType.NoInternet,
                ErrorMessage = "WebSocket not connected"
            });
            return;
        }

        try
        {
            string encryptedMessage = _security.Encrypt(message);
            
            // 如果需要回调，添加callbackId
            if (callback != null)
            {
                var messageObj = JsonUtility.FromJson<WebSocketMessage>(message);
                messageObj.callbackId = Guid.NewGuid().ToString();
                _webSocketCallbacks[messageObj.callbackId] = callback;
                encryptedMessage = _security.Encrypt(JsonUtility.ToJson(messageObj));
            }
            
            _webSocket.Send(encryptedMessage);
        }
        catch (Exception e)
        {
            Debug.LogError($"WebSocket send error: {e.Message}");
            callback?.Invoke(new NetworkResponse
            {
                Success = false,
                ErrorType = NetworkErrorType.ServerError,
                ErrorMessage = e.Message
            });
        }
    }

    public bool IsWebSocketConnected => _webSocketConnected;

    #endregion

    #region Utility Methods

    private string GetAuthToken()
    {
        // 从玩家Prefs或内存中获取认证token
        return PlayerPrefs.GetString("auth_token", _secretKey);
    }

    #endregion
}

[Serializable]
public class WebSocketMessage
{
    public string type;
    public string data;
    public string callbackId;
}