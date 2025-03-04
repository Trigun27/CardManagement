namespace CardManagement.Api

open System
open Microsoft.AspNetCore.Hosting
open Microsoft.Extensions.Configuration
open Microsoft.Extensions.Logging
open Microsoft.AspNetCore.Builder
open Microsoft.Extensions.DependencyInjection
open Giraffe
open CardManagement.Infrastructure
open CardManagement.Common
open Errors
open ErrorMessages
open Serilog

module Program =
    open Newtonsoft.Json
    open Newtonsoft.Json.Serialization

    type [<CLIMutable>] ErrorModel = { Error: string }
    let toErrorModel str = { Error = str }
    let notFound f = json { Error = "Not found."} f

    let errorHandler (ex: Exception) (logger:  Microsoft.Extensions.Logging.ILogger) =
        match ex with
        | :? Newtonsoft.Json.JsonReaderException ->
            clearResponse
            >=> RequestErrors.BAD_REQUEST ex.Message
        | _ ->
            logger.LogError(EventId(), ex, "An unhandled exception has occurred while executing the request.")
            ex.GetType().FullName |> printf "%s"
            clearResponse
            >=> ServerErrors.INTERNAL_ERROR ex.Message

    let errorToResponse e =
        let message = errorMessage e |> toErrorModel |> json
        match e with
        | Bug exn ->
            Log.Error(exn.ToString())
            let err = toErrorModel "Oops. Something went wrong" |> json
            ServerErrors.internalError err
        | OperationNotAllowed _
        | ValidationError _ -> RequestErrors.unprocessableEntity message
        | DataError e ->
            match e with
            | EntityNotFound _ -> RequestErrors.notFound message
            | _ -> RequestErrors.unprocessableEntity message

    let resultToHttpResponseAsync asyncWorkflow : HttpHandler =
        fun next ctx ->
        task {
            let! result = asyncWorkflow |> Async.StartAsTask
            let responseFn =
                match result with
                | Ok ok -> json ok |> Successful.ok
                | Error e -> errorToResponse e
            return! responseFn next ctx
        }

    let optionToHttpResponseAsync asyncWorkflow : HttpHandler =
        fun next ctx ->
        task {
            let! result = asyncWorkflow |> Async.StartAsTask
            let responseFn =
                match result with
                | Ok (Some ok) -> json ok |> Successful.ok
                | Ok None -> RequestErrors.notFound notFound
                | Error e -> errorToResponse e
            return! responseFn next ctx
        }
    let bindJsonForRoute<'a> r f = routeCi r >=> bindJson<'a> f
    let webApp =
        choose [
            GET >=>
                choose [
                    routeCif "/users/%O" (fun userId -> CardApi.getUser userId |> optionToHttpResponseAsync)
                    routeCif "/cards/%s" (fun cardNumber -> CardApi.getCard cardNumber |> optionToHttpResponseAsync)
                ]
            PATCH >=>
                choose [
                    bindJsonForRoute "/cards/deactivate"
                        (fun cmd -> CardApi.deactivateCard cmd |> resultToHttpResponseAsync)
                    bindJsonForRoute "/cards/activate"
                        (fun cmd -> CardApi.activateCard cmd |> resultToHttpResponseAsync)
                    bindJsonForRoute "/cards/setDailyLimit"
                        (fun cmd -> CardApi.setDailyLimit (DateTimeOffset.UtcNow,cmd) |> resultToHttpResponseAsync)
                    bindJsonForRoute "/cards/processPayment"
                        (fun cmd -> CardApi.processPayment (DateTimeOffset.UtcNow,cmd) |> resultToHttpResponseAsync)
                    bindJsonForRoute "/cards/topUp"
                        (fun cmd -> CardApi.topUp (DateTimeOffset.UtcNow,cmd) |> resultToHttpResponseAsync)
                ]
            POST >=>
                choose [
                    bindJsonForRoute "/users"
                        (fun cmd -> CardApi.createUser (Guid.NewGuid(),cmd) |> resultToHttpResponseAsync)
                    bindJsonForRoute "/cards"
                        (fun cmd -> CardApi.createCard cmd |> resultToHttpResponseAsync)
                ]
            RequestErrors.notFound notFound
        ]

    let configureApp (app : IApplicationBuilder) =
        // Add Giraffe to the ASP.NET Core pipeline
        app.UseGiraffeErrorHandler(errorHandler)
           .UseGiraffe webApp

    let configureServices (services : IServiceCollection) =
        // Add Giraffe dependencies
        services.AddGiraffe() |> ignore

        let customSettings = JsonSerializerSettings()
        customSettings.Converters.Add(OptionConverter())
        let contractResolver = CamelCasePropertyNamesContractResolver()
        customSettings.ContractResolver <- contractResolver

        services.AddSingleton<Json.ISerializer>(NewtonsoftJson.Serializer(customSettings)) |> ignore

    [<EntryPoint>]
    let main args =
        AppConfiguration.configureLog()
        WebHostBuilder()
            .UseKestrel()
            .Configure(Action<IApplicationBuilder> configureApp)
            .ConfigureServices(configureServices)
            .Build()
            .Run()

        0
