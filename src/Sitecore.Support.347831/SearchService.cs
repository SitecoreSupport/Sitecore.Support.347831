// Sitecore.XA.Foundation.Search.Services.SearchService
using Microsoft.Extensions.DependencyInjection;
using Sitecore.ContentSearch;
using Sitecore.ContentSearch.Linq.Utilities;
using Sitecore.ContentSearch.Utilities;
using Sitecore.Data;
using Sitecore.Data.Fields;
using Sitecore.Data.Items;
using Sitecore.DependencyInjection;
using Sitecore.Globalization;
using Sitecore.Sites;
using Sitecore.XA.Foundation.Abstractions;
using Sitecore.XA.Foundation.Multisite;
using Sitecore.XA.Foundation.Search;
using Sitecore.XA.Foundation.Search.Extensions;
using Sitecore.XA.Foundation.Search.Models;
using Sitecore.XA.Foundation.Search.Services;
using Sitecore.XA.Foundation.SitecoreExtensions.Extensions;
using Sitecore.XA.Foundation.SitecoreExtensions.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
namespace Sitecore.Support.XA.Foundation.Search.Services
{

    public class SearchService : ISearchService
    {
        protected ISearchContextService SearchContextService
        {
            get;
            set;
        }

        protected IMultisiteContext MultisiteContext
        {
            get;
            set;
        }

        protected ISortingService SortingService
        {
            get;
            set;
        }

        protected IFacetService FacetService
        {
            get;
            set;
        }

        protected IIndexResolver IndexResolver
        {
            get;
            set;
        }

        protected IContext Context
        {
            get;
        }

        protected IBoostingService BoostingService
        {
            get;
            set;
        }

        public bool IsGeolocationRequest => Context.Request.QueryString.AllKeys.Contains("g");

        public SearchService()
        {
            SearchContextService = ServiceLocator.ServiceProvider.GetService<ISearchContextService>();
            MultisiteContext = ServiceLocator.ServiceProvider.GetService<IMultisiteContext>();
            SortingService = ServiceLocator.ServiceProvider.GetService<ISortingService>();
            FacetService = ServiceLocator.ServiceProvider.GetService<IFacetService>();
            IndexResolver = ServiceLocator.ServiceProvider.GetService<IIndexResolver>();
            Context = ServiceLocator.ServiceProvider.GetService<IContext>();
            BoostingService = ServiceLocator.ServiceProvider.GetService<IBoostingService>();
        }

        public IEnumerable<Item> Search(string query = null, string scopeId = null, string language = null, string sortOrder = null, int pageSize = 20, int offset = 0, Coordinates center = null, string site = null, string itemid = null)
        {
            IQueryable<ContentPage> query2 = GetQuery(query, scopeId, language, center, site, itemid);
            query2 = SortingService.Order(query2, sortOrder, center, site);
            query2 = query2.Skip(offset);
            query2 = query2.Take(pageSize);
            return Enumerable.Where(from r in query2
                                    select r.GetItem(), (Item i) => i != null);
        }

        public IQueryable<ContentPage> GetQuery(string query, string scope, string language, Coordinates center, string site, string itemid, out string indexName)
        {
            ISearchIndex searchIndex = IndexResolver.ResolveIndex();
            IList<Item> list = ItemUtils.Lookup(scope, Context.Database);
            searchIndex.CreateSearchContext();
            Item contextItem = GetContextItem(itemid);
            indexName = searchIndex.Name;
            IEnumerable<SearchStringModel> models = (from i in list
                                                     select i["ScopeQuery"]).SelectMany(SearchStringModel.ParseDatasourceString);
            models = ResolveSearchQueryTokens(contextItem, models);
            IQueryable<ContentPage> source;
            using (new SiteContextSwitcher(SiteContextFactory.GetSiteContext("shell")))
            {
                source = LinqHelper.CreateQuery<ContentPage>(searchIndex.CreateSearchContext(), models);
            }
            source = source.Where(IsGeolocationRequest ? GeolocationPredicate(site) : PageOrMediaPredicate(site));
            source = source.Where(ContentPredicate(query));
            source = source.Where(LanguagePredicate(language));
            source = source.Where(LatestVersionPredicate());
            source = source.ApplyFacetFilters(Context.Request.QueryString, center, site);
            return BoostingService.BoostQuery(list, query, contextItem, source);
        }

        protected virtual IEnumerable<SearchStringModel> ResolveSearchQueryTokens(Item contextItem, IEnumerable<SearchStringModel> models)
        {
            ISearchQueryTokenResolver service = ServiceLocator.ServiceProvider.GetService<ISearchQueryTokenResolver>();
            List<SearchStringModel> query = models.ToList();
            return service.Resolve(query, contextItem);
        }

        protected virtual IQueryable<ContentPage> GetQuery(string query, string scope, string language, Coordinates center, string site, string itemid)
        {
            string indexName;
            return GetQuery(query, scope, language, center, site, itemid, out indexName);
        }

        protected virtual Expression<Func<ContentPage, bool>> PageOrMediaPredicate(string siteName)
        {
            Item homeItem = SearchContextService.GetHomeItem(siteName);
            if (homeItem == null)
            {
                return PredicateBuilder.False<ContentPage>();
            }
            string homeShortId = homeItem.ID.ToSearchID();
            Expression<Func<ContentPage, bool>> expression = (ContentPage i) => i.RawPath == homeShortId && i.IsSearchable;
            Item settingsItem = MultisiteContext.GetSettingsItem(homeItem);
            if (settingsItem != null)
            {
                MultilistField multilistField = settingsItem.Fields[Sitecore.XA.Foundation.Search.Templates._SearchCriteria.Fields.AssociatedContent];
                if (multilistField != null)
                {
                    foreach (string id in from i in multilistField.TargetIDs
                                          select i.ToSearchID())
                    {
                        expression = expression.Or((ContentPage i) => i.RawPath == id && i.IsSearchable);
                    }
                }
                MultilistField multilistField2 = settingsItem.Fields[Sitecore.XA.Foundation.Search.Templates._SearchCriteria.Fields.AssociatedMedia];
                if (multilistField2 != null)
                {
                    foreach (string shortId in from i in multilistField2.GetItems()
                                               select i.ID.ToSearchID())
                    {
                        expression = expression.Or((ContentPage i) => i.RawPath == shortId);
                    }
                    return expression;
                }
            }
            return expression;
        }

        protected virtual Expression<Func<ContentPage, bool>> GeolocationPredicate(string siteName)
        {
            Item homeItem = SearchContextService.GetHomeItem(siteName);
            Item siteItem = MultisiteContext.GetSiteItem(homeItem);
            if (homeItem == null || siteItem == null)
            {
                return PredicateBuilder.False<ContentPage>();
            }
            string siteShortId = siteItem.ID.ToSearchID();
            Expression<Func<ContentPage, bool>> expression = (ContentPage i) => i.RawPath == siteShortId && i.IsPoi;
            Item settingsItem = MultisiteContext.GetSettingsItem(homeItem);
            if (settingsItem != null)
            {
                MultilistField multilistField = settingsItem.Fields[Sitecore.XA.Foundation.Search.Templates._SearchCriteria.Fields.AssociatedContent];
                if (multilistField != null)
                {
                    foreach (string id in from i in multilistField.TargetIDs
                                          select i.ToSearchID())
                    {
                        #region SITECORE SUPPORT PATCH 347831 CHANGES
                        // Added "expression =", code was using LINQ 'Or' on expression but not actually setting it.
                        expression = expression.Or((ContentPage i) => i.RawPath == id && i.IsPoi);
                        #endregion
                    }
                    return expression;
                }
            }
            return expression;
        }

        protected virtual Expression<Func<ContentPage, bool>> ContentPredicate(string content)
        {
            Expression<Func<ContentPage, bool>> expression = PredicateBuilder.True<ContentPage>();
            if (string.IsNullOrWhiteSpace(content))
            {
                return expression;
            }
            foreach (string item in content.Split().TrimAndRemoveEmpty())
            {
                string t = item;
                expression = expression.And((ContentPage i) => i.AggregatedContent.Contains(t));
            }
            return expression;
        }

        protected virtual Expression<Func<ContentPage, bool>> LatestVersionPredicate()
        {
            return PredicateBuilder.True<ContentPage>().And((ContentPage i) => i.LatestVersion);
        }

        protected virtual Expression<Func<ContentPage, bool>> LanguagePredicate(string language)
        {
            IEnumerable<string> source = (from l in language.ParseLanguages()
                                          select l.Name).ToList();
            if (!source.Any())
            {
                return PredicateBuilder.True<ContentPage>();
            }
            Expression<Func<ContentPage, bool>> seed = PredicateBuilder.False<ContentPage>();
            return source.Aggregate(seed, (Expression<Func<ContentPage, bool>> p, string l) => p.Or((ContentPage i) => i.Language == l));
        }

        protected virtual Item GetContextItem(string itemId)
        {
            Item result = null;
            if (ID.IsID(itemId))
            {
                result = Context.Database.GetItem(new ID(itemId));
            }
            return result;
        }
    }

}