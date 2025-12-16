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
	ProgressBar,
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
}

let canContinue = true;
let toofast = false;

export const ImportLayoutTemplate = (props: ImportTemplateClientProperties): JSX.Element => {
	const [error, setError] = useState<string | null>(null);
	const [file, setFile] = useState<File | null>(null);
	const [state, setState] = useState<string[]>([]);
	const [currentFile, setCurrentFile] = useState({ current: 0, total: 0 });

	const [importKind, setImportKind] = useState('insert');
	const [contactGroup, setContactGroup] = useState<string | undefined>('');
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

	const parseFile = (
		file: File,
		callback: (buffer: ArrayBuffer) => boolean,
		finishedCallback: () => void,
	): void => {
		const fileSize = file.size;
		const chunkSize = 32 * 1024; // bytes
		let offset = 0;
		let chunkReaderBlock:
			| null
			| ((_offset: number, length: number, _file: File) => void) = null;

		chunkReaderBlock = (_offset: number, length: number, _file: File) => {
			const r = new FileReader();
			const blob = _file.slice(_offset, length + _offset);
			r.onload = (evt: ProgressEvent<FileReader>) => {
				if (!canContinue) {
					return;
				}

				if (evt.target === null) {
					setError(Localization.integrations.contactsimporter.messages.errorReading);

					return;
				}
				if (evt.target.error === null && evt.target.result !== null) {
					offset += chunkSize;

					let chunkBuffer: ArrayBuffer;

					if (evt.target.result instanceof ArrayBuffer) {
						chunkBuffer = evt.target.result;
					} else if (typeof evt.target.result === 'string') {
						const encoder = new TextEncoder();
						chunkBuffer = encoder.encode(evt.target.result).buffer;
					} else {
						setError(Localization.integrations.contactsimporter.messages.unexpectedFile);

						return;
					}


					if (!callback(chunkBuffer)) {
						// callback for handling read chunk
						canContinue = false;
						setError(Localization.integrations.contactsimporter.messages.serverError);
						return;
					}
				} else {
					finishedCallback();
					setError(
						evt.target.error?.message ??
						Localization.integrations.contactsimporter.messages.errorReading,
					);

					return;
				}
				if (offset >= fileSize) {
					finishedCallback();
					setState((prev) => [...prev, Localization.integrations.contactsimporter.messages.readComplete]);
					return;
				}

				// off to the next chunk
				if (chunkReaderBlock !== null && canContinue) {
					chunkReaderBlock(offset, chunkSize, file);
				}
			};
			if (toofast) {
				setTimeout(() => {
					r.readAsText(blob);
				}, 3000);
				setState((prev) => [
					...prev,
					Localization.integrations.contactsimporter.messages.uploadPaused,
				]);
				toofast = false;
			} else {
				r.readAsText(blob);
			}
		};

		// now let's start the read with the first block
		chunkReaderBlock(offset, chunkSize, file);
	};

	const onUpload = (): void => {
		if (file === null) {
			setError(Localization.integrations.contactsimporter.messages.noFile);

			return;
		}

		setState([]);

		const port = location.port !== '' ? `:${location.port}` : '';
		const scheme = window.location.protocol === 'http:' ? 'ws' : 'wss';
		const socket = new WebSocket(`${scheme}://${location.hostname}${port}/contactsimport/ws`);
		socket.binaryType = 'blob';
		socket.onerror = (e) => {
			setError(Localization.integrations.contactsimporter.messages.errorUploading);
			console.error(e);
			canContinue = false;
			socket.close();
		};
		socket.onmessage = (event) => {
			const p = JSON.parse(event.data);
			switch (p.type) {
				case 'headerConfirmed': {
					setState((prev) => [
						...prev,
						Localization.integrations.contactsimporter.messages.headersValidated,
					]);

					parseFile(
						file,
						(buffer) => {
							if (socket.readyState === socket.OPEN) {
								socket.send(buffer);
								return true;
							}
							return false;
						},
						() => {
							socket.send(JSON.stringify({ type: 'done' }));
						},
					);

					break;
				}
				case 'toofast': {
					toofast = true;
					break;
				}
				case 'msg': {
					setState((prev) => [...prev, p.payload]);
					break;
				}
				case 'progress': {
					const len = Number.parseInt(p.payload, 10);

					if (len === currentFile.total % (32 * 1024)) {
						canContinue = false;
						setState((prev) => [...prev, Localization.integrations.contactsimporter.messages.uploadComplete]);
						setFile(null);
					}

					setCurrentFile((prev) => ({
						total: prev.total,
						current: prev.current + len,
					}));

					break;
				}
				case 'finished': {
					setState((prev) => [...prev, Localization.integrations.contactsimporter.messages.importComplete]);
					setFile(null);
					canContinue = false;
					if (socket.readyState < socket.CLOSING) {
						socket.close();
					}
					break;
				}
			}
		};
		socket.onopen = (_event) => {
			setError(null);
			setState((prev) => [
				...prev,
				`${Localization.integrations.contactsimporter.messages.sendingFile}: ${getFileSize(file)}`,
			]);
			canContinue = true;

			socket.send(
				JSON.stringify({
					type: 'header',
					payload: {
						importKind,
						contactGroup: contactGroup === null ? null : contactGroup,
						delimiter,
						batchSize,
					},
				}),
			);
		};
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
												setCurrentFile({ current: 0, total: f.size });
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
									(<div style={{ maxWidth: '400px' }}>
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
													value={c.guid}/>
											))}
										</Select>
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

								{currentFile.total > 0 && (
									<div>
										<Headline size={HeadlineSize.S} spacingBottom={Spacing.M}>
											Upload Progress
										</Headline>
										<ProgressBar
											completed={Math.floor(
												(currentFile.current / currentFile.total) * 100,
											)}
										/>
									</div>
								)}

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
