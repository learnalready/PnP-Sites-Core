﻿using System;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Threading;
using System.Web;
using OfficeDevPnP.Core;
using OfficeDevPnP.Core.Diagnostics;
using OfficeDevPnP.Core.Utilities;

namespace Microsoft.SharePoint.Client
{
    public static partial class ClientContextExtensions
    {
#if ONPREMISES
        private const string MicrosoftSharePointTeamServicesHeader = "MicrosoftSharePointTeamServices";
#endif

        /// <summary>
        /// Clones a ClientContext object while "taking over" the security context of the existing ClientContext instance
        /// </summary>
        /// <param name="clientContext">ClientContext to be cloned</param>
        /// <param name="siteUrl">Site url to be used for cloned ClientContext</param>
        /// <returns>A ClientContext object created for the passed site url</returns>
        public static ClientContext Clone(this ClientRuntimeContext clientContext, string siteUrl)
        {
            if (string.IsNullOrWhiteSpace(siteUrl))
            {
                throw new ArgumentException(CoreResources.ClientContextExtensions_Clone_Url_of_the_site_is_required_, nameof(siteUrl));
            }

            return clientContext.Clone(new Uri(siteUrl));
        }


        /// <summary>
        /// 
        /// </summary>
        /// <param name="clientContext"></param>
        /// <param name="retryCount">Number of times to retry the request</param>
        /// <param name="delay">Milliseconds to wait before retrying the request. The delay will be increased (doubled) every retry</param>
        public static void ExecuteQueryRetry(this ClientRuntimeContext clientContext, int retryCount = 10, int delay = 500)
        {
            ExecuteQueryImplementation(clientContext, retryCount, delay);
        }

        private static void ExecuteQueryImplementation(ClientRuntimeContext clientContext, int retryCount = 10, int delay = 500)
        {
            var clientTag = string.Empty;
            if (clientContext is PnPClientContext)
            {
                retryCount = (clientContext as PnPClientContext).RetryCount;
                delay = (clientContext as PnPClientContext).Delay;
                clientTag = (clientContext as PnPClientContext).ClientTag;
            }

            int retryAttempts = 0;
            int backoffInterval = delay;
            if (retryCount <= 0)
                throw new ArgumentException("Provide a retry count greater than zero.");

            if (delay <= 0)
                throw new ArgumentException("Provide a delay greater than zero.");

            // Do while retry attempt is less than retry count
            while (retryAttempts < retryCount)
            {
                try
                {
                    // ClientTag property is limited to 32 chars
                    if (string.IsNullOrEmpty(clientTag))
                    {
                        clientTag = $"{PnPCoreUtilities.PnPCoreVersionTag}:{GetCallingPnPMethod()}";
                    }
                    if (clientTag.Length > 32)
                    {
                        clientTag = clientTag.Substring(0, 32);
                    }
                    clientContext.ClientTag = clientTag;

                    // Make CSOM request more reliable by disabling the return value cache. Given we 
                    // often clone context objects and the default value is
#if !ONPREMISES
                    clientContext.DisableReturnValueCache = true;
#elif SP2016
                    clientContext.DisableReturnValueCache = true;
#endif                
                    // DO NOT CHANGE THIS TO EXECUTEQUERYRETRY
                    clientContext.ExecuteQuery();
                    return;
                }
                catch (WebException wex)
                {
                    var response = wex.Response as HttpWebResponse;
                    // Check if request was throttled - http status code 429
                    // Check is request failed due to server unavailable - http status code 503
                    if (response != null && (response.StatusCode == (HttpStatusCode)429 || response.StatusCode == (HttpStatusCode)503))
                    {
                        Log.Warning(Constants.LOGGING_SOURCE, CoreResources.ClientContextExtensions_ExecuteQueryRetry, backoffInterval);

                        //Add delay for retry
                        Thread.Sleep(backoffInterval);

                        //Add to retry count and increase delay.
                        retryAttempts++;
                        backoffInterval = backoffInterval * 2;
                    }
                    else
                    {
                        Log.Error(Constants.LOGGING_SOURCE, CoreResources.ClientContextExtensions_ExecuteQueryRetryException, wex.ToString());
                        throw;
                    }
                }
            }
            throw new MaximumRetryAttemptedException($"Maximum retry attempts {retryCount}, has be attempted.");
        }


        /// <summary>
        /// Clones a ClientContext object while "taking over" the security context of the existing ClientContext instance
        /// </summary>
        /// <param name="clientContext">ClientContext to be cloned</param>
        /// <param name="siteUrl">Site url to be used for cloned ClientContext</param>
        /// <returns>A ClientContext object created for the passed site url</returns>
        public static ClientContext Clone(this ClientRuntimeContext clientContext, Uri siteUrl)
        {
            if (siteUrl == null)
            {
                throw new ArgumentException(CoreResources.ClientContextExtensions_Clone_Url_of_the_site_is_required_, nameof(siteUrl));
            }

            ClientContext clonedClientContext = new ClientContext(siteUrl);
            clonedClientContext.AuthenticationMode = clientContext.AuthenticationMode;
            clonedClientContext.ClientTag = clientContext.ClientTag;
#if !ONPREMISES
            clonedClientContext.DisableReturnValueCache = clientContext.DisableReturnValueCache;
#elif SP2016
            clonedClientContext.DisableReturnValueCache = clientContext.DisableReturnValueCache;
#endif


            // In case of using networkcredentials in on premises or SharePointOnlineCredentials in Office 365
            if (clientContext.Credentials != null)
            {
                clonedClientContext.Credentials = clientContext.Credentials;
            }
            else
            {
                //Take over the form digest handling setting
                clonedClientContext.FormDigestHandlingEnabled = (clientContext as ClientContext).FormDigestHandlingEnabled;

                // In case of app only or SAML
                clonedClientContext.ExecutingWebRequest += delegate (object oSender, WebRequestEventArgs webRequestEventArgs)
                {
                    // Call the ExecutingWebRequest delegate method from the original ClientContext object, but pass along the webRequestEventArgs of 
                    // the new delegate method
                    MethodInfo methodInfo = clientContext.GetType().GetMethod("OnExecutingWebRequest", BindingFlags.Instance | BindingFlags.NonPublic);
                    object[] parametersArray = new object[] { webRequestEventArgs };
                    methodInfo.Invoke(clientContext, parametersArray);
                };
            }

            return clonedClientContext;
        }

        /// <summary>
        /// Gets a site collection context for the passed web. This site collection client context uses the same credentials
        /// as the passed client context
        /// </summary>
        /// <param name="clientContext">Client context to take the credentials from</param>
        /// <returns>A site collection client context object for the site collection</returns>
        public static ClientContext GetSiteCollectionContext(this ClientRuntimeContext clientContext)
        {
            Site site = (clientContext as ClientContext).Site;
            if (!site.IsObjectPropertyInstantiated("Url"))
            {
                clientContext.Load(site);
                clientContext.ExecuteQueryRetry();
            }
            return clientContext.Clone(site.Url);
        }

        /// <summary>
        /// Checks if the used ClientContext is app-only
        /// </summary>
        /// <param name="clientContext">The ClientContext to inspect</param>
        /// <returns>True if app-only, false otherwise</returns>
        public static bool IsAppOnly(this ClientRuntimeContext clientContext)
        {
            if (clientContext.Credentials == null)
            {
                return true;
            }

            return false;
        }

        /// <summary>
        /// Defines a Maximum Retry Attemped Exception
        /// </summary>
        [Serializable]
        public class MaximumRetryAttemptedException : Exception
        {
            /// <summary>
            /// Constructor
            /// </summary>
            /// <param name="message"></param>
            public MaximumRetryAttemptedException(string message)
                : base(message)
            {

            }
        }

        /// <summary>
        /// Checks the server library version of the context for a minimally required version
        /// </summary>
        /// <param name="clientContext"></param>
        /// <param name="minimallyRequiredVersion"></param>
        /// <returns></returns>
        public static bool HasMinimalServerLibraryVersion(this ClientRuntimeContext clientContext, string minimallyRequiredVersion)
        {
            return HasMinimalServerLibraryVersion(clientContext, new Version(minimallyRequiredVersion));
        }

        public static bool HasMinimalServerLibraryVersion(this ClientRuntimeContext clientContext, Version minimallyRequiredVersion)
        {
            bool hasMinimalVersion = false;
#if !ONPREMISES
            try
            {
                clientContext.ExecuteQueryRetry();
                hasMinimalVersion = clientContext.ServerLibraryVersion.CompareTo(minimallyRequiredVersion) >= 0;
            }
            catch (PropertyOrFieldNotInitializedException)
            {
                // swallow the exception.
            }
#else
            try
            {
                HttpWebRequest request = (HttpWebRequest)WebRequest.Create(clientContext.Url);

                var response = request.GetResponse();
                if (response.Headers.AllKeys.Contains(MicrosoftSharePointTeamServicesHeader))
                {
                    var responseVersion = new Version(response.Headers[MicrosoftSharePointTeamServicesHeader]);
                    hasMinimalVersion = responseVersion.CompareTo(minimallyRequiredVersion) >= 0;
                }
            }
            catch (WebException ex)
            {
                var response = ex.Response;
                if (response.Headers.AllKeys.Contains(MicrosoftSharePointTeamServicesHeader))
                {
                    var responseVersion = new Version(response.Headers[MicrosoftSharePointTeamServicesHeader]);
                    hasMinimalVersion = responseVersion.CompareTo(minimallyRequiredVersion) >= 0;
                }
            }
#endif
            return hasMinimalVersion;
        }

        private static string GetCallingPnPMethod()
        {
            StackTrace t = new StackTrace();

            string pnpMethod = "";
            try
            {
                for (int i = 0; i < t.FrameCount; i++)
                {
                    var frame = t.GetFrame(i);
                    if (frame.GetMethod().Name.Equals("ExecuteQueryRetry"))
                    {
                        var method = t.GetFrame(i + 1).GetMethod();

                        // Only return the calling method in case ExecuteQueryRetry was called from inside the PnP core library
                        if (method.Module.Name.Equals("OfficeDevPnP.Core.dll", StringComparison.InvariantCultureIgnoreCase))
                        {
                            pnpMethod = method.Name;
                        }
                        break;
                    }
                }
            }
            catch
            {
                // ignored
            }

            return pnpMethod;
        }

    }
}
