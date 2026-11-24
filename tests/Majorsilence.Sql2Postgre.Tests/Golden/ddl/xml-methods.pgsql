-- sql2pg: phase Schemas

-- sql2pg:object Schema mssql_compat status=Converted
CREATE SCHEMA IF NOT EXISTS mssql_compat;

-- sql2pg: phase Compat

-- sql2pg:object Function mssql_compat.xml_modify status=Converted
-- xml.modify() (WP 4.24). PostgreSQL's xml type has no update functions, so the document is
-- rebuilt from its text. The target is found with xpath (the caller's XPath 1.0 path and
-- namespace array), as the number of elements (or text nodes) before it in document order; the
-- document's text is then split into tags and text, and the change is made at that node.
-- As in SQL Server: a NULL document fails, a target that is not there leaves the document as it
-- is, and replacing a value with an empty sequence (NULL) fails.

-- An xs:double as SQL Server writes it: 15 significant digits, in decimal notation from 1E-6 up
-- to 1E6 and in scientific notation (1.0E6, 9.0E-7, 0.0E0) outside it.
CREATE OR REPLACE FUNCTION mssql_compat.xquery_double(x float8) RETURNS text
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    s text;
    m numeric;
    e int;
BEGIN
    IF x IS NULL THEN
        RETURN NULL;
    ELSIF x = 'NaN'::float8 THEN
        RETURN 'NaN';
    ELSIF x = 'Infinity'::float8 THEN
        RETURN 'INF';
    ELSIF x = '-Infinity'::float8 THEN
        RETURN '-INF';
    ELSIF x = 0 THEN
        RETURN CASE WHEN x::text LIKE '-%' THEN '-0.0E0' ELSE '0.0E0' END;
    END IF;
    s := trim(to_char(x, '9.99999999999999EEEE'));
    m := split_part(s, 'e', 1)::numeric;
    e := split_part(s, 'e', 2)::int;
    IF e BETWEEN -6 AND 5 THEN
        RETURN trim_scale(m * power(10::numeric, e))::text;
    END IF;
    s := trim_scale(m)::text;
    IF position('.' IN s) = 0 THEN
        s := s || '.0';
    END IF;
    RETURN s || 'E' || e;
END $$;

-- The document's text as tags, comments, processing instructions, CDATA sections and text, in order.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_tokens(doc text) RETURNS TABLE (n bigint, tok text)
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT t.n, t.m[1]
    FROM regexp_matches(doc,
        '(<!--(?:[^-]|-[^-])*-->|<\?(?:[^?]|\?+[^?>])*\?+>|<!\[CDATA\[(?:[^]]|\][^]]|\]\]+[^]>])*\]\]+>|</?[^<>"'']*(?:(?:"[^"]*"|''[^'']*'')[^<>"'']*)*>|[^<]+)',
        'g') WITH ORDINALITY AS t(m, n)
$$;

-- Where the k-th element (node_kind 'element', counted from 0 in document order) or the k-th
-- text node inside the root ('text') is in the text: its start tag (or text) and, for an element
-- that is not empty (<a/>), its end tag; for a text node, the tokens around it. Positions count
-- characters from 1.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_locate(doc text, node_kind text, k bigint,
    OUT start_at int, OUT start_tag text, OUT end_at int, OUT end_tag text, OUT before_tok text, OUT after_tok text)
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    pos int := 1;
    depth int := 0;
    seen bigint := 0;
    found_depth int;
    found_text boolean := false;
    previous text;
    t text;
BEGIN
    FOR t IN SELECT x.tok FROM mssql_compat.xml_modify_tokens(doc) AS x ORDER BY x.n LOOP
        IF found_text THEN
            after_tok := t;
            RETURN;
        END IF;
        IF left(t, 1) <> '<' OR left(t, 9) = '<![CDATA[' THEN
            IF node_kind = 'text' AND depth > 0 THEN
                IF seen = k THEN
                    start_at := pos;
                    start_tag := t;
                    before_tok := previous;
                    found_text := true;
                END IF;
                seen := seen + 1;
            END IF;
        ELSIF left(t, 2) = '</' THEN
            depth := depth - 1;
            IF found_depth IS NOT NULL AND depth = found_depth THEN
                end_at := pos;
                end_tag := t;
                RETURN;
            END IF;
        ELSIF left(t, 2) NOT IN ('<?', '<!') THEN
            IF node_kind = 'element' AND found_depth IS NULL THEN
                IF seen = k THEN
                    start_at := pos;
                    start_tag := t;
                    IF right(t, 2) = '/>' THEN
                        RETURN;
                    END IF;
                    found_depth := depth;
                END IF;
                seen := seen + 1;
            END IF;
            IF right(t, 2) <> '/>' THEN
                depth := depth + 1;
            END IF;
        END IF;
        pos := pos + length(t);
        previous := t;
    END LOOP;
    IF found_text THEN
        RETURN;
    END IF;
    RAISE EXCEPTION 'xml modify(): the target node was not found in the document''s text';
END $$;

-- The string value of an XPath expression (xpath returns it escaped).
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_string(path text, doc xml, ns text[]) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT replace(replace(replace(replace(replace((xpath('string(' || path || ')', doc, ns))[1]::text,
        '&lt;', '<'), '&gt;', '>'), '&quot;', '"'), '&apos;', ''''), '&amp;', '&')
$$;

-- Text content and attribute values written as SQL Server writes them.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_escape(s text, in_attribute boolean) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT CASE WHEN in_attribute
        THEN replace(replace(replace(replace(replace(replace(replace(s, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), '"', '&quot;'),
            chr(9), '&#x09;'), chr(10), '&#x0A;'), chr(13), '&#x0D;')
        ELSE replace(replace(replace(replace(s, '&', '&amp;'), '<', '&lt;'), '>', '&gt;'), chr(13), '&#x0D;')
    END
$$;

-- An empty element (<a/>, <a b="1" />) written with a start and an end tag around content.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_expand(start_tag text, content text) RETURNS text
LANGUAGE sql IMMUTABLE PARALLEL SAFE AS $$
    SELECT regexp_replace(start_tag, '\s*/>$', '>') || content || '</' || substring(start_tag FROM '^<([^\s/>]+)') || '>'
$$;

-- insert <node> (as first | as last) into | before | after (target)[1]. place is 'first',
-- 'last', 'before' or 'after'; node_ns is the namespace of the node's unprefixed element names
-- (the prolog's default element namespace, or ''), declared on it when the namespace in scope
-- where it goes differs.
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_insert(doc xml, target text, ns text[], node text, node_ns text, place text) RETURNS xml
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    t text;
    k bigint;
    in_scope text;
    head text;
    loc record;
BEGIN
    IF doc IS NULL THEN
        RAISE EXCEPTION 'Mutator ''modify()'' cannot be called on a null value.' USING ERRCODE = 'null_value_not_allowed';
    END IF;
    IF NOT xpath_exists('(' || target || ')[1]/self::*', doc, ns) THEN
        RETURN doc;
    END IF;
    k := (xpath('count((' || target || ')[1]/preceding::*) + count((' || target || ')[1]/ancestor::*)', doc, ns))[1]::text::bigint;
    in_scope := mssql_compat.xml_modify_string('(' || target || ')[1]' || CASE WHEN place IN ('before', 'after') THEN '/..' ELSE '' END
        || '/namespace::*[name()='''']', doc, ns);
    IF coalesce(in_scope, '') <> coalesce(node_ns, '') THEN
        head := substring(node FROM '^<[^\s/>]+');
        node := head || ' xmlns="' || mssql_compat.xml_modify_escape(coalesce(node_ns, ''), true) || '"' || substr(node, length(head) + 1);
    END IF;
    t := doc::text;
    loc := mssql_compat.xml_modify_locate(t, 'element', k);
    IF place = 'before' THEN
        RETURN overlay(t PLACING node FROM loc.start_at FOR 0)::xml;
    ELSIF place = 'after' THEN
        RETURN overlay(t PLACING node FROM coalesce(loc.end_at + length(loc.end_tag), loc.start_at + length(loc.start_tag)) FOR 0)::xml;
    ELSIF loc.end_at IS NULL THEN
        RETURN overlay(t PLACING mssql_compat.xml_modify_expand(loc.start_tag, node) FROM loc.start_at FOR length(loc.start_tag))::xml;
    ELSIF place = 'first' THEN
        RETURN overlay(t PLACING node FROM loc.start_at + length(loc.start_tag) FOR 0)::xml;
    ELSE
        RETURN overlay(t PLACING node FROM loc.end_at FOR 0)::xml;
    END IF;
END $$;

-- replace value of (target)[1] with new_value. target_kind is 'text' (a text node), 'element'
-- (typed xml: an element of simple type, its content replaced) or 'attribute' (attribute_name,
-- without a prefix, of the element the target's parent step finds).
CREATE OR REPLACE FUNCTION mssql_compat.xml_modify_replace(doc xml, target text, ns text[], target_kind text, attribute_name text, new_value text) RETURNS xml
LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE AS $$
DECLARE
    t text;
    k bigint;
    loc record;
    head text;
    rest text;
    tag text;
    a text[];
BEGIN
    IF doc IS NULL THEN
        RAISE EXCEPTION 'Mutator ''modify()'' cannot be called on a null value.' USING ERRCODE = 'null_value_not_allowed';
    END IF;
    IF NOT xpath_exists('(' || target || ')[1]', doc, ns) THEN
        RETURN doc;
    END IF;
    IF new_value IS NULL THEN
        RAISE EXCEPTION 'XQuery: Replacing the value of a node with an empty sequence is allowed only if ''()'' is used as the new value expression.'
            USING ERRCODE = 'null_value_not_allowed';
    END IF;
    t := doc::text;
    IF target_kind = 'text' THEN
        k := (xpath('count((' || target || ')[1]/preceding::text())', doc, ns))[1]::text::bigint;
        loc := mssql_compat.xml_modify_locate(t, 'text', k);
        -- An element whose only content was the text becomes empty, as SQL Server writes it: <t/>.
        IF new_value = '' AND loc.before_tok ~ '^<[^/!?]' AND right(loc.before_tok, 2) <> '/>' AND left(loc.after_tok, 2) = '</' THEN
            RETURN overlay(t PLACING regexp_replace(loc.before_tok, '\s*>$', '/>')
                FROM loc.start_at - length(loc.before_tok) FOR length(loc.before_tok) + length(loc.start_tag) + length(loc.after_tok))::xml;
        END IF;
        RETURN overlay(t PLACING mssql_compat.xml_modify_escape(new_value, false) FROM loc.start_at FOR length(loc.start_tag))::xml;
    END IF;
    IF target_kind = 'attribute' THEN
        target := '(' || target || ')[1]/..';
    END IF;
    k := (xpath('count((' || target || ')[1]/preceding::*) + count((' || target || ')[1]/ancestor::*)', doc, ns))[1]::text::bigint;
    loc := mssql_compat.xml_modify_locate(t, 'element', k);
    IF target_kind = 'element' THEN
        IF new_value = '' THEN
            RETURN overlay(t PLACING regexp_replace(loc.start_tag, '\s*/?>$', '/>')
                FROM loc.start_at FOR coalesce(loc.end_at + length(loc.end_tag), loc.start_at + length(loc.start_tag)) - loc.start_at)::xml;
        END IF;
        IF loc.end_at IS NULL THEN
            RETURN overlay(t PLACING mssql_compat.xml_modify_expand(loc.start_tag, mssql_compat.xml_modify_escape(new_value, false))
                FROM loc.start_at FOR length(loc.start_tag))::xml;
        END IF;
        RETURN overlay(t PLACING mssql_compat.xml_modify_escape(new_value, false)
            FROM loc.start_at + length(loc.start_tag) FOR loc.end_at - loc.start_at - length(loc.start_tag))::xml;
    END IF;
    head := substring(loc.start_tag FROM '^<[^\s/>]+');
    rest := substr(loc.start_tag, length(head) + 1);
    tag := head;
    FOR a IN SELECT m FROM regexp_matches(rest, '(\s+)([^\s=/>]+)(\s*=\s*)("[^"]*"|''[^'']*'')', 'g') AS m LOOP
        tag := tag || a[1] || a[2] || a[3]
            || CASE WHEN a[2] = attribute_name THEN '"' || mssql_compat.xml_modify_escape(new_value, true) || '"' ELSE a[4] END;
    END LOOP;
    tag := tag || substring(rest FROM '\s*/?>$');
    RETURN overlay(t PLACING tag FROM loc.start_at FOR length(loc.start_tag))::xml;
END $$;

-- sql2pg: phase Tables

-- sql2pg:object Table public.candidate status=Converted
CREATE TABLE public.candidate (
    candidateid integer NOT NULL,
    resume xml,
    contact xml
);

-- sql2pg:object Table public.model status=Converted
CREATE TABLE public.model (
    modelid integer NOT NULL,
    instructions xml
);

-- sql2pg: phase Code
SET check_function_bodies = off;

-- sql2pg:object Procedure public.search status=Converted
CREATE FUNCTION public.search(phone varchar(20))
RETURNS TABLE (candidateid integer, addresses xml)
LANGUAGE plpgsql
VOLATILE
AS $$
#variable_conflict use_column
BEGIN
    RETURN QUERY SELECT CAST(candidateid AS integer) AS candidateid, CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:resume' AS "d"), '/' PASSING (resume) COLUMNS v xml PATH '/d:Resume/d:Address' DEFAULT CAST('' AS xml))) AS xml) AS addresses
        FROM public.candidate
        WHERE (SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:resume' AS "d"), '/' PASSING (resume) COLUMNS v boolean PATH 'boolean(/d:Resume/d:Address[d:Addr.Type = "Home" and d:Years > 2])')) = true AND (SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:act' AS "act"), '/' PASSING (contact) COLUMNS v boolean PATH 'boolean(//act:number)')) <> false;
END;
$$;

-- sql2pg:object Procedure public.touch status=Manual
CREATE FUNCTION public.touch(id integer)
RETURNS TABLE (echo integer)
LANGUAGE plpgsql
VOLATILE
AS $$
#variable_conflict use_column
BEGIN
    UPDATE public.candidate
    SET resume = mssql_compat.xml_modify_insert(candidate.resume, '(/Resume)[1]', CAST('{}' AS text[]), '<Seen/>', '', 'first')
    WHERE candidateid = touch.id;
    /* sql2pg:untranslated S2P3041
    SELECT Resume.value('sql:variable("@id")', 'int') AS Echo FROM dbo.Candidate;
    */
END;
$$;

-- sql2pg:object View public.vcandidate status=Converted
CREATE VIEW public.vcandidate AS
SELECT c.candidateid, CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:resume' AS "d"), '/*' PASSING (resume.ref) COLUMNS v text PATH '(/d:Resume/d:Name/d:Name.First)[1]')) AS varchar(30)) AS "name.first", CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:resume' AS "d"), '/*' PASSING (resume.ref) COLUMNS v text PATH '(d:Address/d:Addr.Type)[1]')) AS varchar(30)) AS "addr.type", (SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:resume' AS "d"), '/*' PASSING (resume.ref) COLUMNS v integer PATH 'd:Years[1]')) AS years
FROM public.candidate AS c CROSS JOIN XMLTABLE(XMLNAMESPACES('urn:resume' AS "d"), '/d:Resume' PASSING (c.resume) COLUMNS ref xml PATH '.') AS resume;

-- sql2pg:object View public.vcontact status=Converted
CREATE VIEW public.vcontact AS
SELECT candidateid, CAST((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:ci' AS "ci", 'urn:act' AS "act"), '/*' PASSING (contactinfo.ref) COLUMNS v text PATH '(act:telephoneNumber)[1]/act:number')) AS varchar(50)) AS phone, ltrim(rtrim((SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:act' AS "act"), '/*' PASSING (contactinfo.ref) COLUMNS v text PATH '(act:eMail/act:SpecialInstructions/text())[1]')))) AS note
FROM public.candidate LEFT JOIN XMLTABLE(XMLNAMESPACES('urn:ci' AS "ci"), '/ci:AdditionalContactInfo' PASSING (contact) COLUMNS ref xml PATH '.') AS contactinfo ON true
WHERE contact IS NOT NULL;

-- sql2pg:object View public.vsteps status=Converted
CREATE VIEW public.vsteps AS
SELECT modelid, (SELECT v FROM XMLTABLE(XMLNAMESPACES('urn:m' AS "d"), '/' PASSING (instructions) COLUMNS v text PATH '(/d:root/text())[1]')) AS intro, (SELECT v FROM XMLTABLE('/*' PASSING (loc.ref) COLUMNS v integer PATH '@LocationID[1]')) AS locationid, (SELECT v FROM XMLTABLE('/*' PASSING (loc.ref) COLUMNS v numeric(9,4) PATH '@LaborHours[1]')) AS laborhours, CAST((SELECT v FROM XMLTABLE('/*' PASSING (steps.ref) COLUMNS v text PATH 'string(.)')) AS varchar(1024)) AS step
FROM public.model CROSS JOIN XMLTABLE(XMLNAMESPACES('urn:m' AS "d"), '/d:root/d:Location' PASSING (instructions) COLUMNS ref xml PATH '.') AS loc CROSS JOIN XMLTABLE(XMLNAMESPACES('urn:m' AS "d"), '/*/d:step' PASSING (loc.ref) COLUMNS ref xml PATH '.') AS steps;

-- sql2pg: phase Constraints

-- sql2pg:object Constraint public.candidate.candidate_pkey status=Converted
ALTER TABLE public.candidate ADD CONSTRAINT candidate_pkey PRIMARY KEY (candidateid);

-- sql2pg:object Constraint public.model.model_pkey status=Converted
ALTER TABLE public.model ADD CONSTRAINT model_pkey PRIMARY KEY (modelid);
