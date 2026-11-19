SELECT CAST(xml_rows AS text) AS "XML_F52E2B61-18A1-11d1-B105-00805F49916B"
FROM (SELECT xmlagg(xmlelement(name "row", xmlattributes(xml_value_1 AS "name"))) AS xml_rows FROM (SELECT name AS xml_value_1 FROM shop.customer) AS for_xml_rows) AS for_xml;
