using System.Net;

using Common.Interfaces.EventBus;

using LiteNetLib;

using Microsoft.Extensions.Logging;

using ProcessorApplication.Models.Messages;

namespace ProcessorApplication.Services;
public class MessageHandlerService
{
    private readonly ILogger<MessageHandlerService> _logger;
    private readonly IServiceProvider _serviceProvider;
    private readonly Dictionary<string, List<Type>> _handlerMap;

    public MessageHandlerService(
        ILogger<MessageHandlerService> logger, 
        IServiceProvider serviceProvider)
    {
        _logger = logger;
        _serviceProvider = serviceProvider;
        _handlerMap = new Dictionary<string, List<Type>>();

        var handlers = serviceProvider.GetAllServices<NodeMessageHandler>();
        foreach (var handler in handlers)
        {
            if (handler != null)
            {
                foreach(var action in handler.GetActions())
                {
                    if (!_handlerMap.ContainsKey(action))
                        _handlerMap.Add(action, new List<Type>());

                    _handlerMap[action].Add(action.GetType());
                }
                _logger.LogInformation("Registered handler: {Handler}", handler.GetType().Name);
            }
        }
    }

    public async Task HandleMessage(IPEndPoint sender, ActionMessage messageType, string msg)
    {
        if (_handlerMap.ContainsKey(messageType.Action))
        {
            foreach (var handler in _handlerMap[messageType.Action])
            {
                var handlerInst = _serviceProvider.GetService(handler);
                if(handlerInst != null)
                    await ((NodeMessageHandler)handlerInst).HandleUnconnectedAsync(sender, messageType.Action, msg);
            }
        }
        else
        {
            _logger.LogWarning("No handler for message type: {Type}", messageType.Action);
        }
    }


    public async Task HandleMessage(NetPeer sender, ActionMessage messageType, string msg)
    {
        if (_handlerMap.ContainsKey(messageType.Action))
        {
            foreach (var handler in _handlerMap[messageType.Action])
            {
                var handlerInst = _serviceProvider.GetService(handler);
                if (handlerInst != null)
                    await ((NodeMessageHandler)handlerInst).HandlePeerAsync(sender, messageType.Action, msg);
            }
        }
        else
        {
            _logger.LogWarning("No handler for message type: {Type}", messageType.Action);
        }
    }
}
