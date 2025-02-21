using System.Threading.Tasks;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.AspNetCore.Http;
using Newtonsoft.Json.Linq;
using System;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Microsoft.WindowsAzure.Storage;
using Microsoft.WindowsAzure.Storage.Table;
using System.Linq;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;
using Fluid;
using Fluid.Values;

namespace DurableFunctionsMonitor.DotNetBackend
{
    public static class Orchestration
    {
        // Handles orchestration instance operations.
        // GET  /a/p/i/orchestrations('<id>')
        [FunctionName(nameof(GetOrchestrationFunction))]
        public static async Task<IActionResult> GetOrchestrationFunction(
            // Using /a/p/i route prefix, to let Functions Host distinguish api methods from statics
            [HttpTrigger(AuthorizationLevel.Anonymous, "get", Route = "a/p/i/orchestrations('{instanceId}')")] HttpRequest req,
            string instanceId,
            [DurableClient(TaskHub = "%DFM_HUB_NAME%")] IDurableClient durableClient,
            ILogger log)
        {
            // Checking that the call is authenticated properly
            try
            {
                await Auth.ValidateIdentityAsync(req.HttpContext.User, req.Headers);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to authenticate request");
                return new UnauthorizedResult();
            }

            var status = await GetInstanceStatus(instanceId, durableClient, log);
            if (status == null)
            {
                return new NotFoundObjectResult($"Instance {instanceId} doesn't exist");
            }

            return status.ToJsonContentResult(Globals.FixUndefinedsInJson);
        }

        // Handles orchestration instance operations.
        // POST /a/p/i/orchestrations('<id>')/purge
        // POST /a/p/i/orchestrations('<id>')/rewind
        // POST /a/p/i/orchestrations('<id>')/terminate
        // POST /a/p/i/orchestrations('<id>')/raise-event
        // POST /a/p/i/orchestrations('<id>')/set-custom-status
        // POST /a/p/i/orchestrations('<id>')/restart
        [FunctionName(nameof(PostOrchestrationFunction))]
        public static async Task<IActionResult> PostOrchestrationFunction(
            // Using /a/p/i route prefix, to let Functions Host distinguish api methods from statics
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "a/p/i/orchestrations('{instanceId}')/{action?}")] HttpRequest req,
            string instanceId,
            string action,
            [DurableClient(TaskHub = "%DFM_HUB_NAME%")] IDurableClient durableClient, 
            ILogger log)
        {
            // Checking that the call is authenticated properly
            try
            {
                await Auth.ValidateIdentityAsync(req.HttpContext.User, req.Headers);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to authenticate request");
                return new UnauthorizedResult();
            }

            string bodyString = await req.ReadAsStringAsync();

            switch (action)
            {
                case "purge":
                    await durableClient.PurgeInstanceHistoryAsync(instanceId);
                    break;
                case "rewind":
                    await durableClient.RewindAsync(instanceId, bodyString);
                    break;
                case "terminate":
                    await durableClient.TerminateAsync(instanceId, bodyString);
                    break;
                case "raise-event":
                    dynamic bodyObject = JObject.Parse(bodyString);
                    string eventName = bodyObject.name;
                    JObject eventData = bodyObject.data;

                    await durableClient.RaiseEventAsync(instanceId, eventName, eventData);
                    break;
                case "set-custom-status":

                    string connectionString = Environment.GetEnvironmentVariable(EnvVariableNames.AzureWebJobsStorage);
                    string hubName = Environment.GetEnvironmentVariable(EnvVariableNames.DFM_HUB_NAME);

                    // Updating the table directly, as there is no other known way
                    var tableClient = CloudStorageAccount.Parse(connectionString).CreateCloudTableClient();
                    var table = tableClient.GetTableReference($"{hubName}Instances");

                    var orcEntity = (await table.ExecuteAsync(TableOperation.Retrieve(instanceId, string.Empty))).Result as DynamicTableEntity;

                    if (string.IsNullOrEmpty(bodyString))
                    {
                        orcEntity.Properties.Remove("CustomStatus");
                    }
                    else
                    {
                        // Ensuring that it is at least a valid JSON
                        string customStatus = JObject.Parse(bodyString).ToString();
                        orcEntity.Properties["CustomStatus"] = new EntityProperty(customStatus);
                    }

                    await table.ExecuteAsync(TableOperation.Replace(orcEntity));

                    break;
                case "restart":
                    bool restartWithNewInstanceId = ((dynamic)JObject.Parse(bodyString)).restartWithNewInstanceId;

                    await durableClient.RestartAsync(instanceId, restartWithNewInstanceId);
                    break;
                default:
                    return new NotFoundResult();
            }

            return new OkResult();
        }

        // Renders a custom tab liquid template for this instance and returns the resulting HTML.
        // Why is it POST and not GET? Exactly: because we don't want to allow to navigate to this page directly (bypassing Content Security Policies)
        // POST /a/p/i/orchestrations('<id>')/custom-tab-markup
        [FunctionName(nameof(GetOrchestrationTabMarkupFunction))]
        public static async Task<IActionResult> GetOrchestrationTabMarkupFunction(
            // Using /a/p/i route prefix, to let Functions Host distinguish api methods from statics
            [HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = "a/p/i/orchestrations('{instanceId}')/custom-tab-markup('{templateName}')")] HttpRequest req,
            string instanceId,
            string templateName,
            [DurableClient(TaskHub = "%DFM_HUB_NAME%")] IDurableClient durableClient,
            ILogger log)
        {
            // Checking that the call is authenticated properly
            try
            {
                await Auth.ValidateIdentityAsync(req.HttpContext.User, req.Headers);
            }
            catch (Exception ex)
            {
                log.LogError(ex, "Failed to authenticate request");
                return new UnauthorizedResult();
            }

            var status = await GetInstanceStatus(instanceId, durableClient, log);
            if (status == null)
            {
                return new NotFoundObjectResult($"Instance {instanceId} doesn't exist");
            }

            // The underlying Task never throws, so it's OK.
            var templatesMap = DetailedOrchestrationStatus.TabTemplatesTask.Result;

            string templateCode = templatesMap.GetTemplate(status.GetEntityTypeName(), templateName);
            if (templateCode == null)
            {
                return new NotFoundObjectResult("The specified template doesn't exist");
            }

            try
            {
                var fluidTemplate = FluidTemplate.Parse(templateCode);
                var fluidContext = new TemplateContext(status);

                return new ContentResult()
                {
                    Content = fluidTemplate.Render(fluidContext),
                    ContentType = "text/html; charset=UTF-8"
                };
            }
            catch (Exception ex)
            {
                return new BadRequestObjectResult(ex.Message);
            }
        }

        static Orchestration()
        {
            // Some Fluent-related initialization
            TemplateContext.GlobalMemberAccessStrategy.Register<JObject, object>((obj, fieldName) => obj[fieldName]);
            FluidValue.SetTypeMapping(typeof(JObject), obj => new ObjectValue(obj));
            FluidValue.SetTypeMapping(typeof(JValue), obj => FluidValue.Create(((JValue)obj).Value));
        }

        private static async Task<DetailedOrchestrationStatus> GetInstanceStatus(string instanceId, IDurableClient durableClient, ILogger log)
        {
            // Also trying to load SubOrchestrations in parallel
            var subOrchestrationsTask = GetSubOrchestrationsAsync(instanceId);
            
            // Intentionally not awaiting and swallowing potential exceptions
            subOrchestrationsTask.ContinueWith(t => log.LogWarning(t.Exception, "Unable to load SubOrchestrations, but that's OK"),
                TaskContinuationOptions.OnlyOnFaulted);

            var status = await durableClient.GetStatusAsync(instanceId, true, true, true);
            if (status == null)
            {
                return null;
            }

            return new DetailedOrchestrationStatus(status, subOrchestrationsTask);
        }

        // Tries to get all SubOrchestration instanceIds for a given Orchestration
        private static async Task<IEnumerable<HistoryEntity>> GetSubOrchestrationsAsync(string instanceId)
        {
            // Querying the table directly, as there is no other known way
            string connectionString = Environment.GetEnvironmentVariable(EnvVariableNames.AzureWebJobsStorage);
            string hubName = Environment.GetEnvironmentVariable(EnvVariableNames.DFM_HUB_NAME);

            var tableClient = CloudStorageAccount.Parse(connectionString).CreateCloudTableClient();
            var table = tableClient.GetTableReference($"{hubName}History");

            var query = new TableQuery<HistoryEntity>()
                .Where(TableQuery.CombineFilters(
                    TableQuery.GenerateFilterCondition("PartitionKey", QueryComparisons.Equal, instanceId),
                    TableOperators.And,
                    TableQuery.GenerateFilterCondition("EventType", QueryComparisons.Equal, "SubOrchestrationInstanceCreated")
                ));

            return (await table.GetAllAsync(query)).OrderBy(he => he._Timestamp);
        }
    }
}