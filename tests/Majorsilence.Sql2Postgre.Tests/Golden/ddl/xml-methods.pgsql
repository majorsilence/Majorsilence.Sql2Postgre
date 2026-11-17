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
    /* sql2pg:untranslated S2P3040
    UPDATE dbo.Candidate SET Resume.modify('insert <Seen/> as first into (/Resume)[1]') WHERE CandidateId = @id;
    */
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
