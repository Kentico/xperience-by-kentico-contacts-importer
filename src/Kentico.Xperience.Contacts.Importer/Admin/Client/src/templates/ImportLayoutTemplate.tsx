import React, { useState } from 'react';
import Localization from '../localization/localization.json';
import {
	Box,
	Button,
	ButtonSize,
	Cols,
	Column,
	Divider,
	DividerOrientation,
	Headline,
	HeadlineSize,
	Input,
	MenuItem,
	NotificationBarAlert,
	RadioButton,
	RadioGroup,
	RadioGroupSize,
	Row,
	Select,
	Shelf,
	Spacing,
	Stack,
	UploadTile,
	UploadTileSize,
	Paper,
} from '@kentico/xperience-admin-components';

interface ImportTemplateClientProperties {
	readonly contactGroups: Array<{ guid: string; displayName: string }>;
	readonly recipientLists: Array<{ guid: string; displayName: string }>;
}

export const ImportLayoutTemplate = (props: ImportTemplateClientProperties): JSX.Element => {
	const [error, setError] = useState<string | null>(null);
	const [file, setFile] = useState<File | null>(null);
	const [state, setState] = useState<string[]>([]);

	const [importKind, setImportKind] = useState('insert');
	const [contactGroup, setContactGroup] = useState<string | undefined>('');
	const [ recipientList, setRecipientList ] = useState<string | undefined>('');
	const [delimiter, setDelimiter] = useState<string>(',');
	const [batchSize, setBatchSize] = useState<number>(5000);

	const uploadModeOptions = [
		{
			label: Localization.integrations.contactsimporter.content.labels.insert,
			value: 'insert',
		},
		{
			label: Localization.integrations.contactsimporter.content.labels.delete,
			value: 'delete',
		},
	];

	const getFileSize = (file: File): string => {
		const fileSizeBytes = file.size;
		const fileSizeKB = fileSizeBytes / 1024;
		if (fileSizeKB < 0.01) {
			return msg(fileSizeBytes, 'B');
		}

		const fileSizeMB = fileSizeKB / 1024;

		if (fileSizeMB < 0.01) {
			return msg(fileSizeKB, 'KB');
		}

		const fileSizeGB = fileSizeMB / 1024;
		if (fileSizeGB < 0.01) {
			return msg(fileSizeMB, 'MB');
		}

		return msg(fileSizeGB, 'GB');

		function msg(size: number, unit: string): string {
			return `${size.toFixed(2)} ${unit}`;
		}
	};

	const onUpload = (): void => {
		if (file === null) {
			setError(Localization.integrations.contactsimporter.messages.noFile);

			return;
		}

		setError(null);
		setState([`${Localization.integrations.contactsimporter.messages.sendingFile}: ${getFileSize(file)}`]);

		const query = new URLSearchParams({ importKind, delimiter, batchSize: batchSize.toString()});
        
		if (importKind === 'insert') {
			if (contactGroup) {
				query.set('contactGroup', contactGroup);
			}
			if (recipientList) {
				query.set('recipientList', recipientList);
			}
		}

		// The server answers with one JSON message per line while the import runs
		let parsedLength = 0;
		let importEnded = false;

		const handleMessage = (message: { type: string; payload: string }): void => {
			switch (message.type) {
				case 'importStarted': {
					setState((prev) => [...prev, Localization.integrations.contactsimporter.messages.importStarted]);
					break;
				}
				case 'msg': {
					setState((prev) => [...prev, message.payload]);
					break;
				}
				case 'error': {
					importEnded = true;
					setError(Localization.integrations.contactsimporter.messages.importFailed);
					setState((prev) => [...prev, message.payload]);
					break;
				}
				case 'finished': {
					importEnded = true;
					setState((prev) => [...prev, Localization.integrations.contactsimporter.messages.importComplete]);
					setFile(null);
					break;
				}
			}
		};

		const readMessages = (): void => {
			const lastNewLine = xhr.responseText.lastIndexOf('\n');
			if (lastNewLine < parsedLength) {
				return;
			}

			const lines = xhr.responseText.substring(parsedLength, lastNewLine).split('\n');
			parsedLength = lastNewLine + 1;

			for (const line of lines) {
				if (line.trim() !== '') {
					handleMessage(JSON.parse(line));
				}
			}
		};

		const xhr = new XMLHttpRequest();
        
		xhr.upload.onload = () => {
			setState((prev) => [...prev, Localization.integrations.contactsimporter.messages.uploadComplete]);
		};
        
		xhr.onprogress = () => {
			if (xhr.status === 200) {
				readMessages();
			}
		};
        
		xhr.onload = () => {
			if (xhr.status !== 200) {
				setError(xhr.responseText || Localization.integrations.contactsimporter.messages.errorUploading);
				return;
			}

			readMessages();

			if (!importEnded) {
				setError(Localization.integrations.contactsimporter.messages.serverError);
			}
		};
        
		xhr.onerror = () => {
			setError(Localization.integrations.contactsimporter.messages.errorUploading);
		};

		xhr.open('POST', `/contactsimport/upload?${query}`);
		xhr.setRequestHeader('Content-Type', 'text/csv');
		xhr.send(file);
	};

	return (
		<Box spacing={Spacing.M}>
			<Headline size={HeadlineSize.L} spacingBottom={Spacing.M}>
				{Localization.integrations.contactsimporter.content.headlines.main}
			</Headline>
			<Row spacing={Spacing.XL}>
				<Column
					cols={Cols.Col12}
					colsMd={Cols.Col10}
					colsLg={Cols.Col8}
					order={Cols.Col2}
					orderLg={Cols.Col1}>
					<Box spacingBottom={Spacing.M}>
						{error !== null && (
							<NotificationBarAlert onDismiss={() => setError(null)}>
								{error}
							</NotificationBarAlert>
						)}
					</Box>
					<Paper>
						<Box spacing={Spacing.XL}>
							<Stack spacing={Spacing.XL} fullHeight={true}>
								<Box spacingY={Spacing.M}>
									<style>{`.dropzone___rGl2g { padding: 10px; }`}</style>
									<UploadTile
										acceptFiles='.csv'
										firstLineLabel={Localization.integrations.contactsimporter.content.labels.uploadFileLine1}
										secondLineLabel={Localization.integrations.contactsimporter.content.labels.uploadFileLine2}
										buttonLabel={Localization.integrations.contactsimporter.content.labels.uploadButton}
										size={UploadTileSize.Compact}
										onUpload={([f]) => {
											if (f instanceof File) {
												setFile(f);
											}
										}} />

									{file !== null && (
										<p style={{ color: 'var(--color-text-default-on-light)' }}>
											File Selected: {file.name}
										</p>
									)}
								</Box>

								<RadioGroup
									label={Localization.integrations.contactsimporter.content.labels.importMode}
									name='uploadMode'
									size={RadioGroupSize.Large}
									value={importKind}
									onChange={setImportKind}>
									{uploadModeOptions.map((option, key) => (
										<RadioButton key={option.value} {...option}>
											{option.label}
										</RadioButton>
									))}
								</RadioGroup>
								{importKind === 'insert' &&
									(<div>
										<div style={{ maxWidth: '400px', paddingBottom: '20px' }}>
											<Select
												label={Localization.integrations.contactsimporter.content.labels.contactGroup}
												clearable={true}
												placeholder='(none)'
												onChange={setContactGroup}
												value={contactGroup}
												disabled={props.contactGroups.length === 0}
												explanationText={Localization.integrations.contactsimporter.content.explanations.contactGroup}>

												{props.contactGroups.map((c) => (
													<MenuItem
														primaryLabel={c.displayName}
														key={c.guid}
														value={c.guid}
													/>
												))}
											</Select>
										</div>
										<div style={{ maxWidth: '400px' }}>
											<Select
												label={Localization.integrations.contactsimporter.content.labels.recipientList}
												clearable={true}
												placeholder='(none)'
												onChange={setRecipientList}
												value={recipientList}
												disabled={props.recipientLists.length === 0}
												explanationText={Localization.integrations.contactsimporter.content.explanations.recipientList}
											>
												{props.recipientLists.map((c) => (
													<MenuItem
														primaryLabel={c.displayName}
														key={c.guid}
														value={c.guid}
													/>
												))}
											</Select>
										</div>
									</div>)
								}

								<div style={{ maxWidth: '400px' }}>
									<Input
										label={Localization.integrations.contactsimporter.content.labels.delimiter}
										type='text'
										onChange={(v) => {
											setDelimiter(v.target.value);
										}}
										value={delimiter}
										explanationText={Localization.integrations.contactsimporter.content.explanations.delimiter}/>
								</div>
								<div style={{ maxWidth: '400px' }}>
									<Input
										label={Localization.integrations.contactsimporter.content.labels.batchSize}
										type='number'
										onChange={(v) => {
											setBatchSize(Number.parseInt(v.target.value, 10));
										}}
										value={batchSize}
										min={1}
										explanationText={Localization.integrations.contactsimporter.content.explanations.batchSize}/>
								</div>

								<Button
									label={Localization.integrations.contactsimporter.content.labels.run}
									size={ButtonSize.S}
									onClick={onUpload}
								/>

								<Divider orientation={DividerOrientation.Horizontal} />

								{state.length > 0 && (
									<Shelf>
										<Box spacing={Spacing.M}>
											<Headline size={HeadlineSize.S}>Upload Log</Headline>
											<pre style={{ color: 'var(--color-text-default-on-light)', overflowX: 'scroll' }}>
												{[...state].reverse().join('\r\n')}
											</pre>
										</Box>
									</Shelf>
								)}
							</Stack>
						</Box>
					</Paper>
				</Column>
			</Row>
		</Box>
	);
};
