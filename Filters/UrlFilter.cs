﻿#region

using System;
using System.Collections;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using System.Web;
using System.Web.Mvc;
using Orchard;
using Orchard.Data;
using Orchard.Logging;
using Orchard.Mvc;
using Orchard.Mvc.Filters;
using Orchard.Themes;
using Orchard.UI.Admin;
using Rijkshuisstijl.UrlProtector.Models;
using Rijkshuisstijl.UrlProtector.Services;

#endregion

namespace Rijkshuisstijl.UrlProtector.Filters
{
    public class UrlFilter : FilterProvider, IActionFilter
    {
        private const int MaxFilteredRecordsInDatabase = 10;
        private readonly ICachedUrlProtectorRules _cachedUrlProtectorRules;
        private readonly IRepository<FilteredRequestRecord> _filteredRequestRecords;
        private readonly IOrchardServices _orchardServices;
        private readonly IWorkContextAccessor _wca;

        public UrlFilter(IWorkContextAccessor wca,
            IRepository<FilteredRequestRecord> filteredRequestRecords,
            ICachedUrlProtectorRules cachedUrlProtectorRules,
            IOrchardServices orchardServices)
        {
            _wca = wca;
            _filteredRequestRecords = filteredRequestRecords;
            _cachedUrlProtectorRules = cachedUrlProtectorRules;
            _orchardServices = orchardServices;
            Logger = NullLogger.Instance;
            UserAgent = _wca.GetContext().HttpContext.Request.UserAgent;
            UserHostAddress = _wca.GetContext().HttpContext.Request.UserHostAddress;
            RequestUrl = _wca.GetContext().HttpContext.Request.Url;
        }

        public String UserAgent { get; set; }
        public string UserHostAddress { get; set; }
        public Uri RequestUrl { get; set; }
        public ILogger Logger { get; set; }

        //Executed for every user request
        public void OnActionExecuting(ActionExecutingContext filterContext)
        {
            //Skip check if there is no url request found
            if (RequestUrl == null)
            {
                return;
            }

            #region Dashboard

            //Check dashboard pages or authorisation pages
            foreach (DashboardFilterRecord dashboardFilterRecord in _cachedUrlProtectorRules.DashboardFilterRecords)
            {
                if (AdminFilter.IsApplied(filterContext.RequestContext) || IsAuthenticationUrl(filterContext.ActionDescriptor.ActionName, filterContext.ActionDescriptor.ControllerDescriptor.ControllerName))
                {
                    //Check if url must be redirected to SSL
                    bool mustBeDirectedToSsl = dashboardFilterRecord.ForceSsl && filterContext.HttpContext.Request.Url != null && !filterContext.HttpContext.Request.IsSecureConnection;

                    //Check if userhostaddress and useragent match the needed pattern
                    Regex userHostAddressPattern = new Regex(dashboardFilterRecord.UserHostAddressPattern, RegexOptions.IgnoreCase);
                    Regex userAgentPattern = new Regex(dashboardFilterRecord.UserAgentPattern);

                    if (userHostAddressPattern.IsMatch(UserHostAddress) && userAgentPattern.IsMatch(UserAgent))
                    {
                        //Userhostaddress and useragent matches the pattern. Access is allowed. Redirect if it must be a SSL session.
                        if (mustBeDirectedToSsl)
                        {
                            filterContext.Result = RedirectToSecure(filterContext.HttpContext.Request.Url);
                        }
                        return;
                    }

                    //No access is granted for this admin request.
                    LogFilteredRequest();

                    //ek:rewrite

                    if (dashboardFilterRecord.ReturnStatusNotFound)
                    {
                        NotFoundResult(filterContext);
                    }
                    else
                    {
                        filterContext.Result = new HttpUnauthorizedResult();
                    }
                    return;
                }
                break;
            }

            #endregion

            #region UrlFilter

            //Check if the url matches a protected url pattern
            foreach (UrlFilterRecord urlFilterRecord in _cachedUrlProtectorRules.UrlFilterRecords.OrderBy(r => r.UrlPriority))
            {
                Regex urlPattern = new Regex(urlFilterRecord.UrlPattern, RegexOptions.IgnoreCase);

                //Do not prevent dashboard with urlfilter settings, it must be done with the dashboard item to prevent patterns that exclude the dashboard by accident
                if (!urlPattern.IsMatch(RequestUrl.AbsolutePath) | AdminFilter.IsApplied(filterContext.RequestContext))
                {
                    continue;
                }

                //Url matches the pattern

                //check if url must be forced to SSL
                bool mustBeDirectedToSsl = urlFilterRecord.ForceSsl && filterContext.HttpContext.Request.Url != null && !filterContext.HttpContext.Request.IsSecureConnection;

                //Check if userhostaddress and useragent match the needed pattern
                Regex userHostAddressPattern = new Regex(urlFilterRecord.UserHostAddressPattern, RegexOptions.IgnoreCase);
                Regex userAgentPattern = new Regex(urlFilterRecord.UserAgentPattern);

                if (userHostAddressPattern.IsMatch(UserHostAddress) && userAgentPattern.IsMatch(UserAgent))
                {
                    //Userhostaddress and useragent matches the pattern. Access is allowed. Redirect if it must be a SSL session.

                    if (mustBeDirectedToSsl)
                    {
                        filterContext.Result = RedirectToSecure(filterContext.HttpContext.Request.Url);
                    }
                    return;
                }

                #endregion

               


                switch ((UrlFilterReturnActionsEnum) urlFilterRecord.FailureAction)
                {
                    case UrlFilterReturnActionsEnum.AccessDenied:
                        //No access is granted for this request.
                        LogFilteredRequest();
                        filterContext.Result = new HttpUnauthorizedResult();
                        break;
                    case UrlFilterReturnActionsEnum.NotFound:
                        //No access is granted for this request.
                        LogFilteredRequest();
                        NotFoundResult(filterContext);
                        break;
                    case UrlFilterReturnActionsEnum.InMaintenance:
                        //Action is not logged because it could be normal behavior
                        InMaintenanceResult(filterContext);
                        break;
                    case UrlFilterReturnActionsEnum.Redirect:
                        //Action is not logged because it could be normal behavior
                        filterContext.Result = new RedirectResult(urlFilterRecord.RedirectTo);
                        break;
                   case UrlFilterReturnActionsEnum.NoAction:
                        //Do nothing
                        ;
                        break;
                    default:
                        //it should not happen.
                        Logger.Error(null, "An invalid option is choosen for the urlFilterRecord.FailureAction. Value {0} is unknown.", urlFilterRecord.FailureAction);
                        //Take no action for the user
                        ;
                        break;
                }
                return;
            }
        }

        public void OnActionExecuted(ActionExecutedContext filterContext)
        {
        }

        private void NotFoundResult(ActionExecutingContext filterContext)
        {
            dynamic model = _orchardServices.New.NotFound();
            HttpRequestBase request = filterContext.RequestContext.HttpContext.Request;
            string url = request.RawUrl;

            // If the url is relative then replace with Requested path
            model.RequestedUrl = request.Url != null && request.Url.OriginalString.Contains(url) & request.Url.OriginalString != url ?
                request.Url.OriginalString : url;

            // Dont get the user stuck in a 'retry loop' by
            // allowing the Referrer to be the same as the Request
            model.ReferrerUrl = request.UrlReferrer != null &&
                                request.UrlReferrer.OriginalString != model.RequestedUrl ? request.UrlReferrer.OriginalString : null;

            //Add the default theme
            filterContext.HttpContext.Items[typeof (ThemeFilter)] = null;

            //Remove Admin theme if enabled
            DictionaryEntry adminUiFilter = new DictionaryEntry();
            foreach (DictionaryEntry item in filterContext.HttpContext.Items.Cast<DictionaryEntry>().Where(item => item.Key.ToString() == "Orchard.UI.Admin.AdminFilter"))
            {
                adminUiFilter = item;
            }

            if (adminUiFilter.Key != null && !String.IsNullOrEmpty(adminUiFilter.Key.ToString()))
            {
                filterContext.HttpContext.Items.Remove(adminUiFilter.Key);
            }

            filterContext.Result = new ShapeResult(filterContext.Controller, model);
            filterContext.RequestContext.HttpContext.Response.StatusCode = (int) HttpStatusCode.NotFound;

            // prevent IIS 7.0 classic mode from handling the 404/500 itself
            filterContext.RequestContext.HttpContext.Response.TrySkipIisCustomErrors = true;
        }

        private void InMaintenanceResult(ActionExecutingContext filterContext)
        {
            filterContext.HttpContext.Items[typeof (ThemeFilter)] = null;
            dynamic model = _orchardServices.New.Maintenance();
            filterContext.RequestContext.HttpContext.Response.StatusCode = (int) HttpStatusCode.ServiceUnavailable;
            filterContext.Result = new ShapeResult(filterContext.Controller, model);
        }

        private void LogFilteredRequest()
        {
            int newId;

            //Log in the logfile as a warning
            Logger.Warning("UrlProtector prevented access to url {1} from address {0} with useragent {2}", UserHostAddress, RequestUrl, UserAgent);

            //Log in the database for the list with most recent filtered requests
            //Get the last record id to add a new record and recycle if maximum of records is reached
            FilteredRequestRecord mostRecentRecord = (from filteredRecord in _filteredRequestRecords.Table
                orderby filteredRecord.RequestTime descending
                select filteredRecord).FirstOrDefault();

            if (mostRecentRecord == null || mostRecentRecord.Id > MaxFilteredRecordsInDatabase)
            {
                newId = 1;
            }
            else
            {
                newId = mostRecentRecord.Id + 1;
            }

            FilteredRequestRecord newFilteredRequestRecord = new FilteredRequestRecord
            {
                Id = newId,
                RequestTime = DateTime.Now,
                Url = RequestUrl.AbsolutePath,
                UserAgent = UserAgent,
                UserHostAddress = UserHostAddress
            };

            _filteredRequestRecords.Update(newFilteredRequestRecord);
        }

        private static Boolean IsAuthenticationUrl(String actionName, String controllerName)
        {
            return controllerName == "Account" &&
                   (actionName == "LogOn"
                    || actionName == "ChangePassword"
                    || actionName == "AccessDenied"
                    || actionName == "Register"
                    || actionName.StartsWith("ChallengeEmail", StringComparison.OrdinalIgnoreCase));
        }

        private static RedirectResult RedirectToSecure(Uri requestUrl)
        {
            UriBuilder builder = new UriBuilder(requestUrl)
            {
                Scheme = Uri.UriSchemeHttps,
                Port = 443
            };

            return new RedirectResult(builder.Uri.ToString());
        }
    }
}