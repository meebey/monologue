using System;
using System.Collections;
using System.IO;
using System.Net;
using System.Threading;
using System.Xml;
using System.Xml.Serialization;
using Rss;
using ICSharpCode.SharpZipLib.GZip;

class Settings {
	public static bool Verbose;

	public static void Log (string fmt, params object [] args)
	{
		if (Verbose)
			Console.Error.WriteLine (fmt, args);
	}
}

class MonologueWorker {
	static string bloggersFile;
	static string outputFile;
	static string rssOutFile;
	static DateTime lastReadOfBloggersFile;
	
	static RssFeed outFeed;
	static BloggerCollection bloggers;
	
	static int Main (string [] args)
	{
		if (args.Length < 3 || args.Length > 8) {
			Console.WriteLine ("monologue-worker.exe BLOGGERS_FILE HTML_OUTPUT RSS_OUTPUT " +
						"[--loop [ms to sleep]] [--cachedir dirname] [--verbose]");
			return 1;
		}
		
		bloggersFile = args [0];
		outputFile = args [1];
		rssOutFile = args [2];

		bool loop = false;
		int msToSleep = 0;
		int aLength = args.Length;

		for (int i = 3; i < aLength; i++) {
			if (args [i] == "--verbose") {
				Settings.Verbose = true;
				continue;
			}

			if (args [i] == "--loop") {
				loop = true;
				msToSleep = 600000;
				try {
					msToSleep = int.Parse (args [++i]);
				} catch {
					i--;
				}
				continue;
			}

			if (args [i] == "--cachedir") {
				if (++i >= aLength) {
					Console.WriteLine ("--cachedir needs an argument");
					return 1;
				}

				FeedCache.CacheDir = Path.Combine (Environment.CurrentDirectory, args [i]);
				continue;
			}
			Console.Error.WriteLine ("Invalid argument: {0}", args [i]);
			return 1;
		}
		
		do {
			RunOnce ();
			Thread.Sleep (msToSleep);
		} while (loop);

		return 0;
	}
	
	static void RunOnce ()
	{
		if (bloggers == null || File.GetLastWriteTime (bloggersFile) > lastReadOfBloggersFile) {
			lastReadOfBloggersFile = File.GetLastWriteTime (bloggersFile);
			bloggers = BloggerCollection.LoadFromFile (bloggersFile);
		}
		
		bool somethingChanged = false;
		int [] counters = new int [(int) UpdateStatus.MAX];
		
		foreach (Blogger b in bloggers.BloggersByUrl) {
			UpdateStatus st;
			b.Feed = FeedCache.Read (b.Name, b.RssUrl, out st);
			b.UpdateFeed ();
			counters [(int) st]++;
			if (st != UpdateStatus.Cached && st != UpdateStatus.CachedButError)
				somethingChanged = true;
		}

		for (int i = 0; i < (int) UpdateStatus.MAX; i++) {
			Console.WriteLine ("{0}: {1}", (UpdateStatus) i, counters [i]);
		}

		if (!somethingChanged)
			return;
		
		outFeed = new RssFeed ();
		RssChannel ch = new RssChannel ();
		ch.Title = "Monologue";
		ch.Generator = "Monologue worker: b-diddy powered";
		ch.Description = "The voices of Mono";
		ch.Link = new Uri ("http://www.go-mono.com/monologue/");
		
		ArrayList stories = new ArrayList ();
		
		DateTime minPubDate = DateTime.Now.AddDays (-14);
		foreach (Blogger b in bloggers.BloggersByUrl) {
			if (b.Channel == null) continue;
			foreach (RssItem i in b.Channel.Items) {
				if (i.PubDate >= minPubDate) {
					string realTitle = b.Name + ": " + i.Title;
					i.Title = realTitle;
					stories.Add (i);
				}
			}
		}
		
		stories.Sort (new ReverseRssItemComparer ());
		
		foreach (RssItem itm in stories)
			ch.Items.Add (itm);
		
		outFeed.Channels.Add (ch);
		outFeed.Write (rssOutFile);
		
		Render ();
	}
	
	public class ReverseRssItemComparer : IComparer {
		int IComparer.Compare( Object x, Object y )
		{
			return DateTime.Compare (((RssItem)y).PubDate, ((RssItem)x).PubDate);
		}
	}
	public class ReverseRssItemColComparer : IComparer {
		int IComparer.Compare( Object x, Object y )
		{
			return DateTime.Compare (((RssItem)((ArrayList)y) [0]).PubDate.Date, ((RssItem)((ArrayList)x) [0]).PubDate.Date);
		}
	}
	
	static void Render ()
	{
		Template tpl = new Template("default.tpl");

		tpl.selectSection ("BLOGGER");
		foreach (Blogger b in bloggers.Bloggers) {
			
			tpl.setField ("BLOGGER_URL", b.HtmlUrl.ToString ());
			tpl.setField ("BLOGGER_NAME", b.Name);
			tpl.setField ("BLOGGER_RSSURL", b.RssUrl);
			
			tpl.appendSection ();
		}
		tpl.deselectSection ();

		tpl.selectSection ("BLOG_DAY");
		foreach (ArrayList day in GetDaysCollectionList ()) {
			
			tpl.setField ("DAY_DATE", ((RssItem)day [0]).PubDate.Date.ToString ("M"));

			tpl.selectSection ("DAY_ENTRY");
			foreach (RssItem itm in day) {
				tpl.setField ("ENTRY_LINK", itm.Link.ToString ());
				/*
				Blogger bl = bloggers [itm.Author];
				if (bl != null) {
					tpl.setField ("ENTRY_PERSON", bl.Name);
				} else {
					Settings.Log ("'{0}' have no author", itm.Title);

					int colon = itm.Title.IndexOf (":");
					if (colon != -1) {
						string author = itm.Title.Substring (0, colon);
						bl = bloggers [author];
						if (bl != null) {
							Settings.Log ("Using {0}", author);
							tpl.setField ("ENTRY_PERSON", author);
						}
					}
				}
				*/
				tpl.setField ("ENTRY_TITLE", itm.Title);
				tpl.setField ("ENTRY_HTML", itm.Description);
				tpl.setField ("ENTRY_DATE", itm.PubDate.ToString ("h:mm tt"));

				tpl.appendSection ();
			}
			
			tpl.deselectSection ();
			tpl.appendSection ();
		}
		tpl.deselectSection ();
		
		
		TextWriter w = new StreamWriter (File.Create (outputFile));
		w.Write (tpl.getContent ());
		w.Flush ();
	}
	
	static ArrayList GetDaysCollectionList ()
	{
		Hashtable ht = new Hashtable ();
		
		foreach (RssItem itm in outFeed.Channels [0].Items) {
			ArrayList ar = ht [itm.PubDate.Date] as ArrayList;
			if (ar != null) {
				ar.Add (itm);
			} else {
				ht [itm.PubDate.Date] = ar = new ArrayList ();
				ar.Add (itm);
			}
		}
		
		ArrayList ret = new ArrayList (ht.Values);
		ret.Sort (new ReverseRssItemColComparer ());
		return ret;
	}
}

public class BloggerCollection {
	static XmlSerializer serializer = new XmlSerializer (typeof (BloggerCollection));
	
	public static BloggerCollection LoadFromFile (string file)
	{
		return (BloggerCollection)serializer.Deserialize (new XmlTextReader (file));
	}

	ArrayList bloggers;
	ArrayList bloggersByUrl;
	Hashtable idToBlogger;
	[XmlElement ("Blogger", typeof (Blogger))]
	public ArrayList Bloggers {
		get {
			return bloggers;
		}
		set {
			bloggers = value;
			bloggers.Sort (new BloggerComparer ());
			idToBlogger = new Hashtable ();
			foreach (Blogger b in bloggers)
				idToBlogger.Add (b.ID, b);
		}
	}

	public ArrayList BloggersByUrl {
		get {
			if (bloggersByUrl == null) {
				bloggersByUrl = new ArrayList (bloggers);
				bloggersByUrl.Sort (new UrlComparer ());
			}
			return bloggersByUrl;
		}
	}

	public Blogger this [string id] {
		get {
			return (Blogger)idToBlogger [id];
		}
	}

	public class BloggerComparer : IComparer {
		int IComparer.Compare (object x, object y)
		{
			return String.Compare (((Blogger)x).Name, ((Blogger)y).Name);
		}
	}

	public class UrlComparer : IComparer {
		int IComparer.Compare (object x, object y)
		{
			return String.Compare (((Blogger)x).RssUrl, ((Blogger)y).RssUrl);
		}
	}
}

public enum UpdateStatus {
	Downloaded,
	Updated,
	Cached,
	CachedButError,
	Error,
	MAX
}

public class Blogger {
	[XmlAttribute] public string Name;
	[XmlAttribute] public string RssUrl;
	[XmlIgnore]
	public string ID {
		// Must look like an email to make rss happy
		get { return XmlConvert.EncodeLocalName (Name) + "@" + XmlConvert.EncodeLocalName (RssUrl); }
	}
		
	RssFeed feed;
	[XmlIgnore]
	public RssChannel Channel {
		get {
			if (feed == null)
				return null;
		
			if (feed.Channels.Count == 0)
				return null;
	
			return feed.Channels [0];
		}
	}
	
	[XmlIgnore]
	public Uri HtmlUrl {
		get {
			if (Channel != null)
				return Channel.Link;
			return new Uri ("http://www.go-mono.com/monologue");
		}
	}
	
	public RssFeed Feed {
		set { feed = value; }
	}

        public string Author {
                get {
                        string author;
                        if (Name.IndexOf (' ') == -1)
                                author = Name;
                        else
                                author = Name.Substring (0, Name.IndexOf (' '));

                        return author + "@monologue.go-mono.com";
                }
        }

	public void UpdateFeed ()
	{
		if (feed == null)
			return;

		if (feed.Channels.Count > 0)
			foreach (RssItem i in feed.Channels [0].Items)
				i.Author = Author;
	}
}

public class FeedCache {
	static string cacheDir;

	public static string CacheDir {
		get { return cacheDir; }
		set {
			cacheDir = value;
			Directory.CreateDirectory (cacheDir);
		}
	}

	static string GetName (string name)
	{
		int hash = name.GetHashCode ();
		if (hash < 0)
			hash = -hash;

		return Path.Combine (cacheDir, hash.ToString ());
	}

	public static RssFeed Read (string name, string url, out UpdateStatus st)
	{
		return Read (name, url, out st, 0);
	}

	static RssFeed Read (string name, string url, out UpdateStatus st, int redirects)
	{
		if (redirects > 10) {
			Settings.Log ("Too many redirects.");
			st = UpdateStatus.Error;
			return null;
		}
			
		st = UpdateStatus.Downloaded;
		if (name == null)
			throw new ArgumentNullException ("name");

		Settings.Log ("Getting {0}", url);
		HttpWebRequest req = (HttpWebRequest) WebRequest.Create (url);
		req.Headers ["Accept-Encoding"] = "gzip";
		req.Timeout = 30000;

		string filename = null;
		bool exists = false;
		if (Enabled) {
			filename = GetName (name);
			exists = File.Exists (filename);
			if (exists) {
				req.IfModifiedSince = File.GetLastWriteTime (filename);
				 // This way, a 304 (NotModified) is not an error
				req.AllowAutoRedirect = false;
			}
		}

		HttpWebResponse resp= null;
		try {
			resp = (HttpWebResponse) req.GetResponse ();
		} catch (WebException we) {
			if (we.Response != null)
				we.Response.Close ();

			Settings.Log ("1 {0}", we.Message);
			if (exists) { // ==> Enabled
				Settings.Log ("Using the cached file.");
				st = UpdateStatus.CachedButError;
				return RssFeed.Read (filename);
			}

			st = UpdateStatus.Error;
			return null;
		}

		HttpStatusCode code = resp.StatusCode;
		switch (code) {
		case HttpStatusCode.OK: // 200
			break; // go ahead

		case HttpStatusCode.MovedPermanently: // 301
		case HttpStatusCode.Redirect: // 302
		case HttpStatusCode.SeeOther: //303
		case HttpStatusCode.TemporaryRedirect: // 307
			string location = resp.Headers ["Location"];
			resp.Close ();
			Settings.Log ("Redirecting to {0}", location);
			return Read (name, location, out st, ++redirects);

		case HttpStatusCode.NotModified: // 304
			resp.Close ();
			Settings.Log ("Not modified since {0}.", req.Headers ["If-Modified-Since"]);
			st = UpdateStatus.Cached;
			return RssFeed.Read (filename);

		default:
			resp.Close ();
			Settings.Log ("2 {0} getting {1}", code, url);

			if (exists) {
				Settings.Log ("Using the cached file.");
				st = UpdateStatus.CachedButError;
				return RssFeed.Read (filename);
			}
			st = UpdateStatus.Error;
			return null;
		}

		byte [] buffer = new byte [16384];
		int length = buffer.Length;
		Stream input = null;
		try {
			input = resp.GetResponseStream ();
		} catch (WebException we) {
			Settings.Log ("3 getting response stream {0} for {1}: {2}",
					name, url, we.Message);

			if (exists) {
				Settings.Log ("Using the cached file.");
				st = UpdateStatus.CachedButError;
				return RssFeed.Read (filename);
			}

			st = UpdateStatus.Error;
			return null;
		}

		string cenc = resp.Headers ["Content-Encoding"];
		if (cenc != null) {
			cenc = cenc.ToLower ().Trim ();
			if (cenc == "gzip") {
				Settings.Log ("Gzipped");
				input = new GZipInputStream (input);
			}
		}

		if (filename == null)
			filename = Path.GetTempFileName ();

		File.Delete (filename);
		try {
			using (FileStream f = new FileStream (filename, FileMode.CreateNew)) {
				int nread = 0;
				while ((nread = input.Read (buffer, 0, length)) != 0)
					f.Write (buffer, 0, nread);
			}

			return RssFeed.Read (filename);
		} catch (Exception e) {
			Console.Error.WriteLine ("4 {0} reading {1}", e, url);
			File.Delete (filename);
			st = UpdateStatus.Error;
			return null;
		} finally {
			resp.Close ();
			if (!Enabled)
				File.Delete (filename);
		}
	}

	public static bool Enabled {
		get { return CacheDir != null; }
	}
}

