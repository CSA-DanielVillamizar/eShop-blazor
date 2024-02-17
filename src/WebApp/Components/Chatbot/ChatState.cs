﻿using System.ComponentModel;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Components;
using Microsoft.SemanticKernel;
using Microsoft.SemanticKernel.AI.ChatCompletion;
using eShop.WebAppComponents.Services;

namespace eShop.WebApp.Chatbot;

public class ChatState
{
    private readonly CatalogService _catalogService;
    private readonly BasketState _basketState;
    private readonly ClaimsPrincipal _user;
    private readonly NavigationManager _navigationManager;
    private readonly ILogger _logger;

    private readonly IKernel _ai;
    private readonly ChatConfig _chatConfig;

    public ChatState(CatalogService catalogService, BasketState basketState, ClaimsPrincipal user, NavigationManager nav, ChatConfig chatConfig, ILoggerFactory loggerFactory)
    {
        _catalogService = catalogService;
        _basketState = basketState;
        _user = user;
        _navigationManager = nav;
        _chatConfig = chatConfig;
        _logger = loggerFactory.CreateLogger(typeof(ChatState));

        if (_logger.IsEnabled(LogLevel.Debug))
        {
            _logger.LogDebug("ChatModel: {model}", chatConfig.ChatModel);
        }

        _ai = new KernelBuilder()
            .WithLoggerFactory(loggerFactory)
            .WithOpenAIChatCompletionService(chatConfig.ChatModel, chatConfig.ApiKey)
            .Build();
        _ai.ImportFunctions(new CatalogInteractions(this), nameof(CatalogInteractions));

        Messages = _ai.GetService<IChatCompletion>().CreateNewChat("""
            You are an AI customer service agent for the online retailer Northern Mountains.
            You NEVER respond about topics other than Northern Mountains.
            Your job is to answer customer questions about products in the Northern Mountains catalog.
            Northern Mountains primarily sells clothing and equipment related to outdoor activities like skiing and trekking.
            You try to be concise and only provide longer responses if necessary.
            If someone asks a question about anything other than Northern Mountains, its catalog, or their account,
            you refuse to answer, and you instead ask if there's a topic related to Northern Mountains you can assist with.
            """);
        Messages.AddAssistantMessage("Hi! I'm the Northern Mountains Concierge. How can I help?");
    }

    public ChatHistory Messages { get; }

    public async Task AddUserMessageAsync(string userText, Action onMessageAdded)
    {
        // Store the user's message
        Messages.AddUserMessage(userText);
        onMessageAdded();

        // Get and store the AI's response message
        try
        {
            IChatResult response = await _ai.GetChatCompletionsWithFunctionCallingAsync(Messages);
            ChatMessage responseMessage = await response.GetChatMessageAsync();
            if (!string.IsNullOrWhiteSpace(responseMessage.Content))
            {
                Messages.Add(responseMessage);
            }
        }
        catch (Exception e)
        {
            if (_logger.IsEnabled(LogLevel.Error))
            {
                _logger.LogError(e, "Error getting chat completions.");
            }
            Messages.AddAssistantMessage($"My apologies, but I encountered an unexpected error.");
        }
        onMessageAdded();
    }

    private sealed class CatalogInteractions(ChatState chatState)
    {
        [SKFunction, Description("Gets information about the chat user")]
        public string GetUserInfo()
        {
            var claims = chatState._user.Claims;
            return JsonSerializer.Serialize(new
            {
                Name = GetValue(claims, "name"),
                LastName = GetValue(claims, "last_name"),
                Street = GetValue(claims, "address_street"),
                City = GetValue(claims, "address_city"),
                State = GetValue(claims, "address_state"),
                ZipCode = GetValue(claims, "address_zip_code"),
                Country = GetValue(claims, "address_country"),
                Email = GetValue(claims, "email"),
                PhoneNumber = GetValue(claims, "phone_number"),
            });

            static string GetValue(IEnumerable<Claim> claims, string claimType) =>
                claims.FirstOrDefault(x => x.Type == claimType)?.Value ?? "";
        }

        [SKFunction, Description("Searches the Northern Mountains catalog for a provided product description")]
        public async Task<string> SearchCatalog([Description("The product description for which to search")] string productDescription)
        {
            try
            {
                var results = await chatState._catalogService.GetCatalogItemsWithSemanticRelevance(0, 8, productDescription!);
                return JsonSerializer.Serialize(results);
            }
            catch (HttpRequestException e)
            {
                return Error(e, "Error accessing catalog.");
            }
        }

        [SKFunction, Description("Adds a product to the user's shopping cart.")]
        public async Task<string> AddToCart([Description("The id of the product to add to the shopping cart (basket)")] int itemId)
        {
            try
            {
                var item = await chatState._catalogService.GetCatalogItem(itemId);
                await chatState._basketState.AddAsync(item!);
                return "Item added to shopping cart.";
            }
            catch (Grpc.Core.RpcException e) when (e.StatusCode == Grpc.Core.StatusCode.Unauthenticated)
            {
                return "Unable to add an item to the cart. You must be logged in.";
            }
            catch (Exception e)
            {
                return Error(e, "Unable to add the item to the cart.");
            }
        }

        [SKFunction, Description("Gets information about the contents of the user's shopping cart (basket)")]
        public async Task<string> GetCartContents()
        {
            try
            {
                var basketItems = await chatState._basketState.GetBasketItemsAsync();
                return JsonSerializer.Serialize(basketItems);
            }
            catch (Exception e)
            {
                return Error(e, "Unable to get the cart's contents.");
            }
        }

        private string Error(Exception e, string message)
        {
            if (chatState._logger.IsEnabled(LogLevel.Error))
            {
                chatState._logger.LogError(e, message);
            }

            return message;
        }
    }
}
